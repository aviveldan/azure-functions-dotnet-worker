﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeFixes;
using System.Collections.Immutable;
using System.Threading.Tasks;
using System.Composition;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Simplification;
using System.Threading;
using System.Linq;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using System;
using System.Collections.Generic;

namespace Microsoft.Azure.Functions.Worker.Sdk.Analyzers
{

    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(BindingTypeCodeFixProvider)), Shared]
    public sealed class BindingTypeCodeFixProvider : CodeFixProvider
    {
        public override ImmutableArray<string> FixableDiagnosticIds =>
            ImmutableArray.Create(DiagnosticDescriptors.SupportedBindingType.Id);

        public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

        public override async Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            Diagnostic diagnostic = context.Diagnostics.First();

            TextSpan diagnosticSpan = diagnostic.Location.SourceSpan;
            SyntaxNode root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);

            MethodDeclarationSyntax methodDeclaration = root.FindToken(diagnosticSpan.Start).Parent.AncestorsAndSelf().OfType<MethodDeclarationSyntax>().First();

            var parameters = methodDeclaration.ParameterList.Parameters;

            foreach (var parameter in parameters)
            {
                await AnalyzeForCodeFix(diagnostic, context, parameter);
            }
        }

        private async Task AnalyzeForCodeFix(Diagnostic diagnostic, CodeFixContext context, ParameterSyntax parameter)
        {
            var semanticModel = await context.Document.GetSemanticModelAsync().ConfigureAwait(false);
            var parameterSymbol = semanticModel.GetDeclaredSymbol(parameter);

            foreach (var attribute in parameterSymbol.GetAttributes())
            {
                var attributeType = attribute?.AttributeClass;
                var inputConverterAttributes = GetInputConverterAttributes(semanticModel, attributeType);

                if (inputConverterAttributes.Count <= 0)
                {
                    continue;
                }

                var supportedTypes = GetSupportedTypes(semanticModel, inputConverterAttributes);

                if (supportedTypes.Count <= 0)
                {
                    continue;
                }

                foreach (var supportedType in supportedTypes)
                {
                    // Create a code action for each potential supported type
                    context.RegisterCodeFix(
                        new SupportedBindingTypeCodeAction(context.Document, diagnostic, parameter, supportedType),
                        diagnostic);
                }
            }
        }

        private static List<AttributeData> GetInputConverterAttributes(SemanticModel model, ITypeSymbol attributeType)
        {
            var inputConverterAttributeType = model.Compilation.GetTypeByMetadataName(Constants.Types.InputConverterAttribute);
            return attributeType.GetAttributes()
                .Where(attr => attr.AttributeClass.Equals(inputConverterAttributeType))
                .ToList();
        }

        private static List<string> GetSupportedTypes(SemanticModel model, List<AttributeData> inputConverterAttributes)
        {
            var supportedTypes = new List<string>();

            foreach (var inputConverterAttribute in inputConverterAttributes)
            {
                var converterName = inputConverterAttribute.ConstructorArguments.FirstOrDefault().Value.ToString();
                var converter = model.Compilation.GetTypeByMetadataName(converterName);

                var converterAttributes = converter.GetAttributes();

                var converterHasSupportedTypeAttribute = converterAttributes.Any(a => a.AttributeClass.Name == Constants.Names.SupportedConverterTypeAttribute);
                if (!converterHasSupportedTypeAttribute)
                {
                    // If a converter does not have the `SupportedConverterTypeAttribute`, we don't need to check for supported types
                    continue;
                }

                supportedTypes.AddRange(converterAttributes
                    .Where(a => a.AttributeClass.Name == Constants.Names.SupportedConverterTypeAttribute)
                    .SelectMany(a => a.ConstructorArguments.Select(arg => arg.Value.ToString()))
                    .ToList());
            }

            return supportedTypes;
        }

        /// <summary>
        /// CodeAction implementation which fixes changes async void to async Task as the return type of the method.
        /// </summary>
        private sealed class SupportedBindingTypeCodeAction : CodeAction
        {
            private readonly Document _document;
            private readonly Diagnostic _diagnostic;
            private readonly ParameterSyntax _parameterSyntax;
            private readonly string _supportedType;

            internal SupportedBindingTypeCodeAction(Document document, Diagnostic diagnostic, ParameterSyntax parameterSyntax, string supportedType)
            {
                this._document = document;
                this._diagnostic = diagnostic;
                this._parameterSyntax = parameterSyntax;
                this._supportedType = supportedType;
            }

            public override string Title => $"Bind to {_supportedType}";

            /// null value is fine since we do not have more than one fix action from this code fix provider.
            public override string EquivalenceKey => null;

            /// <summary>
            /// Returns an updated Document where the invalid binding type is replaced with a supported type.
            /// </summary>
            protected override async Task<Document> GetChangedDocumentAsync(CancellationToken cancellationToken)
            {
                TypeSyntax newTypeSyntax = SyntaxFactory.ParseTypeName(_supportedType);
                ParameterSyntax newParameterSyntax = _parameterSyntax.WithType(newTypeSyntax);

                SyntaxNode root = await _document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
                SyntaxNode newRoot = root.ReplaceNode(_parameterSyntax, newParameterSyntax);

                return _document.WithSyntaxRoot(newRoot);
            }
        }
    }
}
