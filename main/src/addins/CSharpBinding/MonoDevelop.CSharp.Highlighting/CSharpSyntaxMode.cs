// 
// SyntaxMode.cs
//  
// Author:
//   Mike Krüger <mkrueger@novell.com>
//
// Copyright (C) 2009 Novell, Inc (http://www.novell.com)
// 
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//

using System;
using System.Linq;
using System.Collections.Generic;
using Mono.TextEditor.Highlighting;
using Mono.TextEditor;
using System.Xml;
using MonoDevelop.Projects;
using MonoDevelop.CSharp.Project;
using MonoDevelop.Ide.Gui;
using MonoDevelop.Ide;
using MonoDevelop.Ide.Tasks;
using MonoDevelop.Ide.TypeSystem;
using MonoDevelop.SourceEditor.QuickTasks;
using System.Threading;
using System.Diagnostics;
using MonoDevelop.Core;
using MonoDevelop.Refactoring;
using Microsoft.CodeAnalysis;
using ICSharpCode.NRefactory6.CSharp.Analysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.CSharp;


namespace MonoDevelop.CSharp.Highlighting
{
	static class StringHelper
	{
		public static bool IsAt (this string str, int idx, string pattern)
		{
			if (idx + pattern.Length > str.Length)
				return false;

			for (int i = 0; i < pattern.Length; i++)
				if (pattern [i] != str [idx + i])
					return false;
			return true;
		}
	}
	
	class CSharpSyntaxMode : SyntaxMode, IQuickTaskProvider, IDisposable
	{
		readonly MonoDevelop.Ide.Gui.Document guiDocument;

		SemanticModel resolver;
		CancellationTokenSource src;

		public bool SemanticHighlightingEnabled {
			get;
			set;
		}

		internal class StyledTreeSegment : TreeSegment
		{
			public string Style {
				get;
				private set;
			}
			
			public StyledTreeSegment (int offset, int length, string style) : base (offset, length)
			{
				Style = style;
			}
		}
		
		class HighlightingSegmentTree : SegmentTree<StyledTreeSegment>
		{
			public bool GetStyle (Chunk chunk, ref int endOffset, out string style)
			{
				var segment = GetSegmentsAt (chunk.Offset).FirstOrDefault ();
				if (segment == null) {
					style = null;
					return false;
				}
				endOffset = segment.EndOffset;
				style = segment.Style;
				return true;
			}
			
			public void AddStyle (int startOffset, int endOffset, string style)
			{
				if (IsDirty)
					return;
				Add (new StyledTreeSegment (startOffset, endOffset - startOffset, style));
			}
		}
		
		Dictionary<DocumentLine, HighlightingVisitior> lineSegments = new Dictionary<DocumentLine, HighlightingVisitior> ();

		public bool DisableConditionalHighlighting {
			get;
			set;
		}

		public static IEnumerable<string> GetDefinedSymbols (MonoDevelop.Projects.Project project)
		{
			var workspace = IdeApp.Workspace;
			if (workspace == null || project == null)
				yield break;
			var configuration = project.GetConfiguration (workspace.ActiveConfiguration) as DotNetProjectConfiguration;
			if (configuration != null) {
				foreach (string s in configuration.GetDefineSymbols ())
					yield return s;
				// Workaround for mcs defined symbol
				if (configuration.TargetRuntime.RuntimeId == "Mono") 
					yield return "__MonoCS__";
			}
		}

		void HandleDocumentParsed (object sender, EventArgs e)
		{
			if (src != null)
				src.Cancel ();
			resolver = null;
			if (guiDocument.IsProjectContextInUpdate) {
				return;
			}
			if (guiDocument != null && SemanticHighlightingEnabled) {
				if (guiDocument.Project != null && guiDocument.IsCompileableInProject) {
					src = new CancellationTokenSource ();
					var newResolverTask = guiDocument.AnalysisDocument.GetSemanticModelAsync ();
					var cancellationToken = src.Token;
					System.Threading.Tasks.Task.Factory.StartNew (delegate {
						if (newResolverTask == null)
							return;
						var newResolver = newResolverTask.Result;
						if (newResolver == null)
							return;
						var visitor = new QuickTaskVisitor (newResolver, cancellationToken);
						try {
							visitor.Visit (newResolver.SyntaxTree.GetRoot ());
						} catch (Exception ex) {
							LoggingService.LogError ("Error while analyzing the file for the semantic highlighting.", ex);
							return;
						}
						if (!cancellationToken.IsCancellationRequested) {
							Gtk.Application.Invoke (delegate {
								if (cancellationToken.IsCancellationRequested)
									return;
								var editorData = guiDocument.Editor;
								if (editorData == null)
									return;
//									compilation = newResolver.Compilation;
								resolver = newResolver;
								quickTasks = visitor.QuickTasks;
								OnTasksUpdated (EventArgs.Empty);
								foreach (var kv in lineSegments) {
									try {
										kv.Value.tree.RemoveListener ();
									} catch (Exception) {
									}
								}
								lineSegments.Clear ();
								var textEditor = editorData.Parent;
								if (textEditor != null) {
									var margin = textEditor.TextViewMargin;
									margin.PurgeLayoutCache ();
									textEditor.QueueDraw ();
								}
							});
						}
					}, cancellationToken);
				}
			}
		}

		class HighlightingVisitior : SemanticHighlightingVisitor<string>
		{
			readonly TextSpan textSpan;
			internal HighlightingSegmentTree tree = new HighlightingSegmentTree ();

			public HighlightingVisitior (SemanticModel resolver, CancellationToken cancellationToken, TextSpan textSpan) : base (resolver)
			{
				if (resolver == null)
					throw new ArgumentNullException ("resolver");
				this.cancellationToken = cancellationToken;
				this.textSpan = textSpan;
				Setup ();
			}

			void Setup ()
			{
				defaultTextColor = "Plain Text";
				referenceTypeColor = "User Types";
				valueTypeColor = "User Types(Value types)";
				interfaceTypeColor = "User Types(Interfaces)";
				enumerationTypeColor = "User Types(Enums)";
				typeParameterTypeColor = "User Types(Type parameters)";
				delegateTypeColor = "User Types(Delegates)";

				methodCallColor = "User Method Usage";
				methodDeclarationColor = "User Method Declaration";

				eventDeclarationColor = "User Event Declaration";
				eventAccessColor = "User Event Usage";

				fieldDeclarationColor ="User Field Declaration";
				fieldAccessColor = "User Field Usage";

				propertyDeclarationColor = "User Property Declaration";
				propertyAccessColor = "User Property Usage";

				variableDeclarationColor = "User Variable Declaration";
				variableAccessColor = "User Variable Usage";

				parameterDeclarationColor = "User Parameter Declaration";
				parameterAccessColor = "User Parameter Usage";

				valueKeywordColor = "Keyword(Context)";
				externAliasKeywordColor = "Keyword(Namespace)";
				varKeywordTypeColor = "Keyword(Type)";

				parameterModifierColor = "Keyword(Parameter)";
				inactiveCodeColor = "Excluded Code";
				syntaxErrorColor = "Syntax Error";

				stringFormatItemColor = "String Format Items";
			}

			protected override void Colorize (TextSpan span, string color)
			{
				if (this.textSpan.IntersectsWith (span)) {
					tree.AddStyle (span.Start, span.End, color);
				}
			}
		}

		class QuickTaskVisitor : CSharpSyntaxVisitor
		{
			internal List<QuickTask> QuickTasks = new List<QuickTask> ();
			readonly SemanticModel resolver;
			readonly CancellationToken cancellationToken;

			public QuickTaskVisitor (SemanticModel resolver, CancellationToken cancellationToken)
			{
				this.resolver = resolver;
				this.cancellationToken = cancellationToken;
			}
			
			public override void VisitBlock (Microsoft.CodeAnalysis.CSharp.Syntax.BlockSyntax node)
			{
				cancellationToken.ThrowIfCancellationRequested ();
				base.VisitBlock (node);
			}
			
			public override void VisitIdentifierName (Microsoft.CodeAnalysis.CSharp.Syntax.IdentifierNameSyntax node)
			{
				base.VisitIdentifierName (node);
				var info = resolver.GetSymbolInfo (node, cancellationToken); 
				if (info.Symbol == null)  {
					QuickTasks.Add (new QuickTask (() => string.Format ("error CS0103: The name `{0}' does not exist in the current context", node.GetText ()), node.SpanStart, ICSharpCode.NRefactory.Refactoring.Severity.Error));
				}
			}
			
//			public override void VisitIdentifierExpression (IdentifierExpression identifierExpression)
//			{
//				base.VisitIdentifierExpression (identifierExpression);
//				var result = resolver.Resolve (identifierExpression, cancellationToken);
//				if (result.IsError) {
//				}
//			}
//
//			public override void VisitMemberReferenceExpression (MemberReferenceExpression memberReferenceExpression)
//			{
//				base.VisitMemberReferenceExpression (memberReferenceExpression);
//				var result = resolver.Resolve (memberReferenceExpression, cancellationToken) as UnknownMemberResolveResult;
//				if (result != null && result.TargetType.Kind != TypeKind.Unknown) {
//					QuickTasks.Add (new QuickTask (string.Format ("error CS0117: `{0}' does not contain a definition for `{1}'", result.TargetType.FullName, memberReferenceExpression.MemberName), memberReferenceExpression.MemberNameToken.StartLocation, Severity.Error));
//				}
//			}
//
//			public override void VisitSimpleType (SimpleType simpleType)
//			{
//				base.VisitSimpleType (simpleType);
//				var result = resolver.Resolve (simpleType, cancellationToken);
//				if (result.IsError) {
//					QuickTasks.Add (new QuickTask (string.Format ("error CS0246: The type or namespace name `{0}' could not be found. Are you missing an assembly reference?", simpleType.Identifier), simpleType.StartLocation, Severity.Error));
//				}
//			}
//
//			public override void VisitMemberType (MemberType memberType)
//			{
//				base.VisitMemberType (memberType);
//				var result = resolver.Resolve (memberType, cancellationToken);
//				if (result.IsError) {
//					QuickTasks.Add (new QuickTask (string.Format ("error CS0246: The type or namespace name `{0}' could not be found. Are you missing an assembly reference?", memberType.MemberName), memberType.StartLocation, Severity.Error));
//				}
//			}
//
//			public override void VisitComment (ICSharpCode.NRefactory.CSharp.Comment comment)
//			{
//			}
		}
		
		static CSharpSyntaxMode ()
		{
			MonoDevelop.Debugger.DebuggingService.DisableConditionalCompilation += DispatchService.GuiDispatch (new EventHandler<MonoDevelop.Ide.Gui.DocumentEventArgs> (OnDisableConditionalCompilation));
			if (IdeApp.Workspace != null) {
				IdeApp.Workspace.ActiveConfigurationChanged += delegate {
					foreach (var doc in IdeApp.Workbench.Documents) {
						TextEditorData data = doc.Editor;
						if (data == null)
							continue;
						// Force syntax mode reparse (required for #if directives)
						var editor = doc.Editor;
						if (editor != null) {
							if (data.Document.SyntaxMode is SyntaxMode) {
								((SyntaxMode)data.Document.SyntaxMode).UpdateDocumentHighlighting ();
								SyntaxModeService.WaitUpdate (data.Document);
							}
							editor.Parent.TextViewMargin.PurgeLayoutCache ();
							doc.ReparseDocument ();
							editor.Parent.QueueDraw ();
						}
					}
				};
			}
			CommentTag.SpecialCommentTagsChanged += (sender, e) => {
				UpdateCommentRule ();
				var actDoc = IdeApp.Workbench.ActiveDocument;
				if (actDoc != null && actDoc.Editor != null) {
					actDoc.UpdateParseDocument ();
					actDoc.Editor.Parent.TextViewMargin.PurgeLayoutCache ();
					actDoc.Editor.Parent.QueueDraw ();
				}
			};
		}
		
		static void OnDisableConditionalCompilation (object s, MonoDevelop.Ide.Gui.DocumentEventArgs e)
		{
			var mode = e.Document.Editor.Document.SyntaxMode as CSharpSyntaxMode;
			if (mode == null)
				return;
			mode.DisableConditionalHighlighting = true;
			e.Document.Editor.Document.CommitUpdateAll ();
		}
		
		static Dictionary<string, string> contextualHighlightKeywords;
		static readonly string[] ContextualKeywords = new string[] {
			"value"
/*			"async",
			"await",
			, //*
			"get", "set", "add", "remove",  //*
			"var", //*
			"global",
			"partial", //* 
			"where",  //*
			"select",
			"group",
			"by",
			"into",
			"from",
			"ascending",
			"descending",
			"orderby",
			"let",
			"join",
			"on",
			"equals"*/
		};

		#region Syntax mode rule cache
		static List<Rule> _rules;
		static List<Mono.TextEditor.Highlighting.Keywords> _keywords;
		static Span[] _spans;
		static Match[] _matches;
		static Marker[] _prevMarker;
		static List<SemanticRule> _SemanticRules;
		static Rule _commentRule;
		static Dictionary<string, Mono.TextEditor.Highlighting.Keywords> _keywordTable;
		static Dictionary<string, Mono.TextEditor.Highlighting.Keywords> _keywordTableIgnoreCase;
		static Dictionary<string, List<string>> _properties;
		#endregion

		static void UpdateCommentRule ()
		{
			if (_commentRule == null)
				return;
			var joinedTasks = string.Join ("", CommentTag.SpecialCommentTags.Select (t => t.Tag));
			_commentRule.SetDelimiter (new string ("&()<>{}[]~!%^*-+=|\\#/:;\"' ,\t.?".Where (c => joinedTasks.IndexOf (c) < 0).ToArray ()));
			_commentRule.Keywords = new[] {
				new Keywords {
					Color = "Comment Tag",
					Words = CommentTag.SpecialCommentTags.Select (t => t.Tag)
				}
			};
		}

		public CSharpSyntaxMode (MonoDevelop.Ide.Gui.Document document)
		{
			this.guiDocument = document;
			guiDocument.DocumentParsed += HandleDocumentParsed;
			SemanticHighlightingEnabled = PropertyService.Get ("EnableSemanticHighlighting", true);
			PropertyService.PropertyChanged += HandlePropertyChanged;
			if (guiDocument.ParsedDocument != null)
				HandleDocumentParsed (this, EventArgs.Empty);

			bool loadRules = _rules == null;

			if (loadRules) {
				var provider = new ResourceStreamProvider (typeof(ResourceStreamProvider).Assembly, typeof(ResourceStreamProvider).Assembly.GetManifestResourceNames ().First (s => s.Contains ("CSharpSyntaxMode")));
				using (var reader = provider.Open ()) {
					SyntaxMode baseMode = SyntaxMode.Read (reader);
					_rules = new List<Rule> (baseMode.Rules.Where (r => r.Name != "Comment"));
					_rules.Add (new Rule {
						Name = "PreProcessorComment"
					});

					_commentRule = new Rule {
						Name = "Comment",
						IgnoreCase = true
					};
					UpdateCommentRule ();

					_rules.Add (_commentRule);
					_keywords = new List<Keywords> (baseMode.Keywords);
					_spans = new List<Span> (baseMode.Spans.Where (span => span.Begin.Pattern != "#")).ToArray ();
					_matches = baseMode.Matches;
					_prevMarker = baseMode.PrevMarker;
					_SemanticRules = new List<SemanticRule> (baseMode.SemanticRules);
					_keywordTable = baseMode.keywordTable;
					_keywordTableIgnoreCase = baseMode.keywordTableIgnoreCase;
					_properties = baseMode.Properties;
				}

				contextualHighlightKeywords = new Dictionary<string, string> ();
				foreach (var word in ContextualKeywords) {
					if (_keywordTable.ContainsKey (word)) {
						contextualHighlightKeywords[word] = _keywordTable[word].Color;
					} else {
						Console.WriteLine ("missing keyword:"+word);
					}
				}

				foreach (var word in ContextualKeywords) {
					_keywordTable.Remove (word);
				}
			}

			rules = _rules;
			keywords = _keywords;
			spans = _spans;
			matches = _matches;
			prevMarker = _prevMarker;
			SemanticRules = _SemanticRules;
			keywordTable = _keywordTable;
			keywordTableIgnoreCase = _keywordTableIgnoreCase;
			properties = _properties;

			if (loadRules) {
				AddSemanticRule ("Comment", new HighlightUrlSemanticRule ("Comment(Line)"));
				AddSemanticRule ("XmlDocumentation", new HighlightUrlSemanticRule ("Comment(Doc)"));
				AddSemanticRule ("String", new HighlightUrlSemanticRule ("String"));
			}
		}

		#region IDisposable implementation

		public void Dispose ()
		{
			if (src != null)
				src.Cancel ();
			guiDocument.DocumentParsed -= HandleDocumentParsed;
			PropertyService.PropertyChanged -= HandlePropertyChanged;
		}

		#endregion

		void HandlePropertyChanged (object sender, PropertyChangedEventArgs e)
		{
			if (e.Key == "EnableSemanticHighlighting")
				SemanticHighlightingEnabled = PropertyService.Get ("EnableSemanticHighlighting", true);
		}

		public override SpanParser CreateSpanParser (DocumentLine line, CloneableStack<Span> spanStack)
		{
			return new CSharpSpanParser (this, spanStack ?? line.StartSpan.Clone ());
		}
		
		public override ChunkParser CreateChunkParser (SpanParser spanParser, ColorScheme style, DocumentLine line)
		{
			return new CSharpChunkParser (this, spanParser, style, line);
		}
		
		abstract class AbstractBlockSpan : Span
		{
			public bool IsValid {
				get;
				private set;
			}
			
			bool disabled;
			
			public bool Disabled {
				get { return disabled; }
				set { disabled = value; SetColor (); }
			}
			
			
			public AbstractBlockSpan (bool isValid)
			{
				IsValid = isValid;
				SetColor ();
				StopAtEol = false;
			}
			
			protected void SetColor ()
			{
				TagColor = "Preprocessor";
				if (disabled || !IsValid) {
					Color = "Excluded Code";
					Rule = "PreProcessorComment";
				} else {
					Color = "Plain Text";
					Rule = "<root>";
				}
			}
		}

		class DefineSpan : Span
		{
			string define;

			public string Define { 
				get { 
					return define;
				}
			}

			public DefineSpan (string define)
			{
				this.define = define;
				StopAtEol = false;
				Color = "Plain Text";
				Rule = "<root>";
			}
		}

		class IfBlockSpan : AbstractBlockSpan
		{
			public IfBlockSpan (bool isValid) : base (isValid)
			{
			}
			
			public override string ToString ()
			{
				return string.Format("[IfBlockSpan: IsValid={0}, Disabled={3}, Color={1}, Rule={2}]", IsValid, Color, Rule, Disabled);
			}
		}
		
		class ElseIfBlockSpan : AbstractBlockSpan
		{
			public ElseIfBlockSpan (bool isValid) : base (isValid)
			{
				base.Begin = new Regex ("#elif");
			}
			
			public override string ToString ()
			{
				return string.Format("[ElseIfBlockSpan: IsValid={0}, Disabled={3}, Color={1}, Rule={2}]", IsValid, Color, Rule, Disabled);
			}
		}
		
		class ElseBlockSpan : AbstractBlockSpan
		{
			public ElseBlockSpan (bool isValid) : base (isValid)
			{
				base.Begin = new Regex ("#else");
			}
			
			public override string ToString ()
			{
				return string.Format("[ElseBlockSpan: IsValid={0}, Disabled={3}, Color={1}, Rule={2}]", IsValid, Color, Rule, Disabled);
			}
		}
		
		protected class CSharpChunkParser : ChunkParser
		{
			HashSet<string> tags = new HashSet<string> ();
			
			CSharpSyntaxMode csharpSyntaxMode;
			int lineNumber;
			public CSharpChunkParser (CSharpSyntaxMode csharpSyntaxMode, SpanParser spanParser, ColorScheme style, DocumentLine line) : base (csharpSyntaxMode, spanParser, style, line)
			{
				lineNumber = line.LineNumber;
				this.csharpSyntaxMode = csharpSyntaxMode;
				foreach (var tag in CommentTag.SpecialCommentTags) {
					tags.Add (tag.Tag);
				}

			}

			protected override void AddRealChunk (Chunk chunk)
			{
				var document = csharpSyntaxMode.guiDocument;
				var parsedDocument = document != null ? document.ParsedDocument : null;
				if (parsedDocument != null && csharpSyntaxMode.SemanticHighlightingEnabled && csharpSyntaxMode.resolver != null) {
					int endLoc = -1;
					string semanticStyle = null;
					if (spanParser.CurSpan != null && (spanParser.CurSpan.Rule == "Comment" || spanParser.CurSpan.Rule == "PreProcessorComment")) {
						base.AddRealChunk (chunk);
						return;
					}

					try {
						HighlightingVisitior visitor;
						if (!csharpSyntaxMode.lineSegments.TryGetValue (line, out visitor)) {
							var resolver = csharpSyntaxMode.resolver;
							visitor = new HighlightingVisitior (resolver, default (CancellationToken), new TextSpan (line.Offset, line.Length));
							visitor.tree.InstallListener (doc);
							visitor.Visit (resolver.SyntaxTree.GetRoot ()); 
							csharpSyntaxMode.lineSegments[line] = visitor;
						}
						string style;
						if (visitor.tree.GetStyle (chunk, ref endLoc, out style)) {
							semanticStyle = style;
						}
					} catch (Exception e) {
						Console.WriteLine ("Error in semantic highlighting: " + e);
					}
					if (semanticStyle != null) {
						if (endLoc < chunk.EndOffset) {
							base.AddRealChunk (new Chunk (chunk.Offset, endLoc - chunk.Offset, semanticStyle));
							base.AddRealChunk (new Chunk (endLoc, chunk.EndOffset - endLoc, chunk.Style));
							return;
						}
						chunk.Style = semanticStyle;
					}
				}
				
				base.AddRealChunk (chunk);
			}
			
			protected override string GetStyle (Chunk chunk)
			{
				if (spanParser.CurRule.Name == "Comment") {
					if (tags.Contains (doc.GetTextAt (chunk))) 
						return "Comment Tag";
				}
				return base.GetStyle (chunk);
			}
		}
		
		protected class CSharpSpanParser : SpanParser
		{
			CSharpSyntaxMode CSharpSyntaxMode {
				get {
					return (CSharpSyntaxMode)mode;
				}
			}
			class ConditinalExpressionEvaluator : ICSharpCode.NRefactory.CSharp.DepthFirstAstVisitor<object, object>
			{
				HashSet<string> symbols;

				MonoDevelop.Projects.Project GetProject (TextDocument doc)
				{
					// There is no reference between document & higher level infrastructure,
					// therefore it's a bit tricky to find the right project.
					
					MonoDevelop.Projects.Project project = null;
					var view = doc.Annotation<MonoDevelop.SourceEditor.SourceEditorView> ();
					if (view != null)
						project = view.Project;
					
					if (project == null) {
						var ideDocument = IdeApp.Workbench.GetDocument (doc.FileName);
						if (ideDocument != null)
							project = ideDocument.Project;
					}
					
					if (project == null)
						project = IdeApp.Workspace.GetProjectContainingFile (doc.FileName);
					
					return project;
				}



				public ConditinalExpressionEvaluator (TextDocument doc, IEnumerable<string> symbols)
				{
					this.symbols = new HashSet<string> (symbols);
					var project = GetProject (doc);
					
					if (project == null) {
						var ideDocument = IdeApp.Workbench.GetDocument (doc.FileName);
						if (ideDocument != null)
							project = ideDocument.Project;
					}
					
					if (project == null)
						project = IdeApp.Workspace.GetProjectContainingFile (doc.FileName);
					
					if (project != null) {
						foreach (var ss in GetDefinedSymbols (project)) {
							if (!symbols.Contains (ss))
								this.symbols.Add (ss);
						}
					}
/*					var parsedDocument = TypeSystemService.ParseFile (document.ProjectContent, doc.FileName, doc.MimeType, doc.Text);
					if (parsedDocument == null)
						parsedDocument = TypeSystemService.ParseFile (dom, doc.FileName ?? "a.cs", delegate { return doc.Text; });
					if (parsedDocument != null) {
						foreach (PreProcessorDefine define in parsedDocument.Defines) {
							symbols.Add (define.Define);
						}
						
					}*/
				}
				
				public override object VisitIdentifierExpression (ICSharpCode.NRefactory.CSharp.IdentifierExpression identifierExpression, object data)
				{
					return symbols.Contains (identifierExpression.Identifier);
				}
				
				public override object VisitUnaryOperatorExpression (ICSharpCode.NRefactory.CSharp.UnaryOperatorExpression unaryOperatorExpression, object data)
				{
					bool result = (bool)(unaryOperatorExpression.Expression.AcceptVisitor (this, data) ?? false);
					if (unaryOperatorExpression.Operator ==  ICSharpCode.NRefactory.CSharp.UnaryOperatorType.Not)
						return !result;
					return result;
				}


				public override object VisitPrimitiveExpression (ICSharpCode.NRefactory.CSharp.PrimitiveExpression primitiveExpression, object data)
				{
					if (primitiveExpression.Value is bool)
						return primitiveExpression.Value;
					return false;
				}

				public override object VisitBinaryOperatorExpression (ICSharpCode.NRefactory.CSharp.BinaryOperatorExpression binaryOperatorExpression, object data)
				{
					bool left = (bool)(binaryOperatorExpression.Left.AcceptVisitor (this, data) ?? false);
					bool right = (bool)(binaryOperatorExpression.Right.AcceptVisitor (this, data) ?? false);
					switch (binaryOperatorExpression.Operator) {
					case ICSharpCode.NRefactory.CSharp.BinaryOperatorType.InEquality:
						return left != right;
					case ICSharpCode.NRefactory.CSharp.BinaryOperatorType.Equality:
						return left == right;
					case ICSharpCode.NRefactory.CSharp.BinaryOperatorType.ConditionalOr:
						return left || right;
					case ICSharpCode.NRefactory.CSharp.BinaryOperatorType.ConditionalAnd:
						return left && right;
					}
					
					Console.WriteLine ("Unknown operator:" + binaryOperatorExpression.Operator);
					return left;
				}

				public override object VisitParenthesizedExpression (ICSharpCode.NRefactory.CSharp.ParenthesizedExpression parenthesizedExpression, object data)
				{
					return parenthesizedExpression.Expression.AcceptVisitor (this, data);
				}

			}
			
			void ScanPreProcessorElse (ref int i)
			{
				if (!spanStack.Any (s => s is IfBlockSpan || s is ElseIfBlockSpan)) {
					base.ScanSpan (ref i);
					return;
				}
				bool previousResult = false;
				foreach (Span span in spanStack) {
					if (span is IfBlockSpan) {
						previousResult = ((IfBlockSpan)span).IsValid;
					}
					if (span is ElseIfBlockSpan) {
						previousResult |= ((ElseIfBlockSpan)span).IsValid;
					}
				}
				//					LineSegment line = doc.GetLineByOffset (i);
				//					int length = line.Offset + line.EditableLength - i;
				while (spanStack.Count > 0 && !(CurSpan is IfBlockSpan || CurSpan is ElseIfBlockSpan)) {
					spanStack.Pop ();
				}
				var ifBlock = CurSpan as IfBlockSpan;
				var elseIfBlock = CurSpan as ElseIfBlockSpan;
				var elseBlockSpan = new ElseBlockSpan (!previousResult);
				if (ifBlock != null) {
					elseBlockSpan.Disabled = ifBlock.Disabled;
				} else if (elseIfBlock != null) {
					elseBlockSpan.Disabled = elseIfBlock.Disabled;
				}
				FoundSpanBegin (elseBlockSpan, i, "#else".Length);
				i += "#else".Length;
					
				// put pre processor eol span on stack, so that '#elif' gets the correct highlight
				Span preprocessorSpan = CreatePreprocessorSpan ();
				FoundSpanBegin (preprocessorSpan, i, 0);
			}
			IEnumerable<string> Defines {
				get {
					if (SpanStack == null)
						yield break;
					foreach (var span in SpanStack) {
						if (span is DefineSpan) {
							var define = ((DefineSpan)span).Define;
							if (define != null)
								yield return define;
						}
					}
				}
			}
			void ScanPreProcessorIf (int textOffset, ref int i)
			{
				var end = CurText.Length;
				int idx = 0;
				while ((idx = CurText.IndexOf ('/', idx)) >= 0 && idx + 1 < CurText.Length) {
					var next = CurText [idx + 1];
					if (next == '/') {
						end = idx - 1;
						break;
					}
					idx++;
				}

				int length = end - textOffset;
				string parameter = CurText.Substring (textOffset + 3, length - 3);
				var expr = new ICSharpCode.NRefactory.CSharp.CSharpParser ().ParseExpression (parameter);
				bool result = false;
				if (expr != null && !expr.IsNull) {
					object o = expr.AcceptVisitor (new ConditinalExpressionEvaluator (doc, Defines), null);
					if (o is bool)
						result = (bool)o;
				}
					
				foreach (Span span in spanStack) {
					if (span is ElseBlockSpan) {
						result &= ((ElseBlockSpan)span).IsValid;
						break;
					}
					if (span is IfBlockSpan) {
						result &= ((IfBlockSpan)span).IsValid;
						break;
					}
					if (span is ElseIfBlockSpan) {
						result &= ((ElseIfBlockSpan)span).IsValid;
						break;
					}
				}
					
				var ifBlockSpan = new IfBlockSpan (result);
					
				foreach (Span span in spanStack) {
					if (span is AbstractBlockSpan) {
						var parentBlock = (AbstractBlockSpan)span;
						ifBlockSpan.Disabled = parentBlock.Disabled || !parentBlock.IsValid;
						break;
					}
				}
					
				FoundSpanBegin (ifBlockSpan, i, length);
				i += length - 1;
			}

			void ScanPreProcessorElseIf (ref int i)
			{
				DocumentLine line = doc.GetLineByOffset (i);
				int length = line.Offset + line.Length - i;
				string parameter = doc.GetTextAt (i + 5, length - 5);
				var expr= new ICSharpCode.NRefactory.CSharp.CSharpParser ().ParseExpression (parameter);
				bool result;
				if (expr != null && !expr.IsNull) {
					var visitResult = expr.AcceptVisitor (new ConditinalExpressionEvaluator (doc, Defines), null);
					result = visitResult != null ? (bool)visitResult : false;
				} else {
					result = false;
				}
					
				IfBlockSpan containingIf = null;
				if (result) {
					bool previousResult = false;
					foreach (Span span in spanStack) {
						if (span is IfBlockSpan) {
							containingIf = (IfBlockSpan)span;
							previousResult = ((IfBlockSpan)span).IsValid;
							break;
						}
						if (span is ElseIfBlockSpan) {
							previousResult |= ((ElseIfBlockSpan)span).IsValid;
						}
					}
						
					result = !previousResult;
				}
					
				var elseIfBlockSpan = new ElseIfBlockSpan (result);
				if (containingIf != null)
					elseIfBlockSpan.Disabled = containingIf.Disabled;
					
				FoundSpanBegin (elseIfBlockSpan, i, 0);
					
				// put pre processor eol span on stack, so that '#elif' gets the correct highlight
				var preprocessorSpan = CreatePreprocessorSpan ();
				FoundSpanBegin (preprocessorSpan, i, 0);
			}

			protected override bool ScanSpan (ref int i)
			{
				if (CSharpSyntaxMode.DisableConditionalHighlighting) {
					return base.ScanSpan (ref i);
				}
				int textOffset = i - StartOffset;

				if (textOffset < CurText.Length && CurRule.Name != "Comment" && CurRule.Name != "String" && CurRule.Name != "VerbatimString" && CurText [textOffset] == '#' && IsFirstNonWsChar (textOffset)) {

					if (CurText.IsAt (textOffset, "#define") && (spanStack == null || !spanStack.Any (span => span is IfBlockSpan && !((IfBlockSpan)span).IsValid))) {
						int length = CurText.Length - textOffset;
						string parameter = CurText.Substring (textOffset + "#define".Length, length - "#define".Length).Trim ();
						var defineSpan = new DefineSpan (parameter);
						FoundSpanBegin (defineSpan, i, 0);
					}
	
					if (CurText.IsAt (textOffset, "#else")) {
						ScanPreProcessorElse (ref i);
						return true;
					}
	
					if (CurText.IsAt (textOffset, "#if")) {
						ScanPreProcessorIf (textOffset, ref i);
						return true;
					}
	
					if (CurText.IsAt (textOffset, "#elif") && spanStack != null && spanStack.Any (span => span is IfBlockSpan)) {
						ScanPreProcessorElseIf (ref i);
						return true;
					}
				}

				return base.ScanSpan (ref i);
			}
			
			public static Span CreatePreprocessorSpan ()
			{
				var result = new Span ();
				result.TagColor = "Preprocessor";
				result.Color = "Preprocessor";
				result.Rule = "String";
				result.StopAtEol = true;
				return result;
			}
			
			void PopCurrentIfBlock ()
			{
				while (spanStack.Count > 0 && (spanStack.Peek () is IfBlockSpan || spanStack.Peek () is ElseIfBlockSpan || spanStack.Peek () is ElseBlockSpan)) {
					var poppedSpan = PopSpan ();
					if (poppedSpan is IfBlockSpan)
						break;
				}
			}
			
			protected override bool ScanSpanEnd (Span cur, ref int i)
			{
				if (cur is IfBlockSpan || cur is ElseIfBlockSpan || cur is ElseBlockSpan) {
					int textOffset = i - StartOffset;
					bool end = CurText.IsAt (textOffset, "#endif");
					if (end) {
						FoundSpanEnd (cur, i, 6); // put empty end tag in
						
						// if we're in a complex span stack pop it up to the if block
						if (spanStack.Count > 0) {
							var prev = spanStack.Peek ();
							
							if ((cur is ElseIfBlockSpan || cur is ElseBlockSpan) && (prev is ElseIfBlockSpan || prev is IfBlockSpan))
								PopCurrentIfBlock ();
						}
					}
					return end;
				}
				return base.ScanSpanEnd (cur, ref i);
			}
			
	//		Span preprocessorSpan;
	//		Rule preprocessorRule;
			
			public CSharpSpanParser (CSharpSyntaxMode mode, CloneableStack<Span> spanStack) : base (mode, spanStack)
			{
//				foreach (Span span in mode.Spans) {
//					if (span.Rule == "text.preprocessor") {
//						preprocessorSpan = span;
//						preprocessorRule = GetRule (span);
//					}
//				}
			}
		}

		#region IQuickTaskProvider implementation
		public event EventHandler TasksUpdated;

		protected virtual void OnTasksUpdated (EventArgs e)
		{
			var handler = TasksUpdated;
			if (handler != null)
				handler (this, e);
		}

		List<QuickTask> quickTasks;
		public IEnumerable<QuickTask> QuickTasks {
			get {
				return quickTasks;
			}
		}
		#endregion
	}
}
 
