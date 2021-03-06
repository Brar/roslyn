﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using Microsoft.CodeAnalysis.CodeStyle;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Shared.Extensions;

namespace Microsoft.CodeAnalysis.MakeFieldReadonly
{
    [DiagnosticAnalyzer(LanguageNames.CSharp, LanguageNames.VisualBasic)]
    internal sealed class MakeFieldReadonlyDiagnosticAnalyzer
        : AbstractCodeStyleDiagnosticAnalyzer
    {
        public MakeFieldReadonlyDiagnosticAnalyzer()
            : base(
                IDEDiagnosticIds.MakeFieldReadonlyDiagnosticId,
                new LocalizableResourceString(nameof(FeaturesResources.Add_readonly_modifier), FeaturesResources.ResourceManager, typeof(FeaturesResources)),
                new LocalizableResourceString(nameof(FeaturesResources.Make_field_readonly), FeaturesResources.ResourceManager, typeof(FeaturesResources)))
        {
        }

        public override bool OpenFileOnly(Workspace workspace) => false;

        public override DiagnosticAnalyzerCategory GetAnalyzerCategory() => DiagnosticAnalyzerCategory.SemanticDocumentAnalysis;

        protected override void InitializeWorker(AnalysisContext context)
        {
            context.RegisterCompilationStartAction(compilationStartContext =>
            {
                // State map for fields:
                //  'isCandidate' : Indicates whether the field is a candidate to be made readonly based on it's options.
                //  'written'     : Indicates if there are any writes to the field outside the constructor and field initializer.
                var fieldStateMap = new ConcurrentDictionary<IFieldSymbol, (bool isCandidate, bool written)>();

                // We register following actions in the compilation:
                // 1. A symbol action for field symbols to ensure the field state is initialized for every field in
                //    the compilation.
                // 2. An operation action for field references to detect if a candidate field is written outside
                //    constructor and field initializer, and update field state accordingly.
                // 3. A symbol start/end action for named types to report diagnostics for candidate fields that were
                //    not written outside constructor and field initializer.

                compilationStartContext.RegisterSymbolAction(AnalyzeFieldSymbol, SymbolKind.Field);

                compilationStartContext.RegisterSymbolStartAction(symbolStartContext =>
                {
                    symbolStartContext.RegisterOperationAction(AnalyzeOperation, OperationKind.FieldReference);
                    symbolStartContext.RegisterSymbolEndAction(OnSymbolEnd);
                }, SymbolKind.NamedType);

                return;

                // Local functions.
                void AnalyzeFieldSymbol(SymbolAnalysisContext symbolContext)
                {
                    _ = TryGetOrInitializeFieldState((IFieldSymbol)symbolContext.Symbol, symbolContext.Options, symbolContext.CancellationToken);
                }

                void AnalyzeOperation(OperationAnalysisContext operationContext)
                {
                    var fieldReference = (IFieldReferenceOperation)operationContext.Operation;
                    (bool isCandidate, bool written) = TryGetOrInitializeFieldState(fieldReference.Field, operationContext.Options, operationContext.CancellationToken);

                    // Ignore fields that are not candidates or have already been written outside the constructor/field initializer.
                    if (!isCandidate || written)
                    {
                        return;
                    }

                    // Check if this is a field write outside constructor and field initializer, and update field state accordingly.
                    if (IsFieldWrite(fieldReference, operationContext.ContainingSymbol))
                    {
                        UpdateFieldStateOnWrite(fieldReference.Field);
                    }
                }

                void OnSymbolEnd(SymbolAnalysisContext symbolEndContext)
                {
                    // Report diagnostics for candidate fields that are not written outside constructor and field initializer.
                    var members = ((INamedTypeSymbol)symbolEndContext.Symbol).GetMembers();
                    foreach (var member in members)
                    {
                        if (member is IFieldSymbol field && fieldStateMap.TryRemove(field, out var value))
                        {
                            (bool isCandidate, bool written) = value;
                            if (isCandidate && !written)
                            {
                                var option = GetCodeStyleOption(field, symbolEndContext.Options, symbolEndContext.CancellationToken);
                                var diagnostic = DiagnosticHelper.Create(
                                    Descriptor,
                                    field.Locations[0],
                                    option.Notification.Severity,
                                    additionalLocations: null,
                                    properties: null);
                                symbolEndContext.ReportDiagnostic(diagnostic);
                            }
                        }
                    }
                }

                bool IsCandidateField(IFieldSymbol symbol) =>
                        symbol.DeclaredAccessibility == Accessibility.Private &&
                        !symbol.IsReadOnly &&
                        !symbol.IsConst &&
                        !symbol.IsImplicitlyDeclared &&
                        symbol.Locations.Length == 1 &&
                        !IsMutableValueType(symbol.Type);

                // Method to update the field state for a candidate field written outside constructor and field initializer.
                void UpdateFieldStateOnWrite(IFieldSymbol field)
                {
                    Debug.Assert(IsCandidateField(field));
                    Debug.Assert(fieldStateMap.ContainsKey(field));

                    fieldStateMap[field] = (isCandidate: true, written: true);
                }

                // Method to get or initialize the field state.
                (bool isCandidate, bool written) TryGetOrInitializeFieldState(IFieldSymbol fieldSymbol, AnalyzerOptions options, CancellationToken cancellationToken)
                {
                    if (!IsCandidateField(fieldSymbol))
                    {
                        return default;
                    }

                    if (fieldStateMap.TryGetValue(fieldSymbol, out var result))
                    {
                        return result;
                    }

                    result = ComputeInitialFieldState(fieldSymbol, options, cancellationToken);
                    return fieldStateMap.GetOrAdd(fieldSymbol, result);
                }

                // Method to compute the initial field state.
                (bool isCandidate, bool written) ComputeInitialFieldState(IFieldSymbol field, AnalyzerOptions options, CancellationToken cancellationToken)
                {
                    Debug.Assert(IsCandidateField(field));

                    var option = GetCodeStyleOption(field, options, cancellationToken);
                    if (option == null || !option.Value)
                    {
                        return default;
                    }

                    return (isCandidate: true, written: false);
                }
            });
        }


        private static CodeStyleOption<bool> GetCodeStyleOption(IFieldSymbol field, AnalyzerOptions options, CancellationToken cancellationToken)
        {
            var optionSet = options.GetDocumentOptionSetAsync(field.Locations[0].SourceTree, cancellationToken).GetAwaiter().GetResult();
            return optionSet?.GetOption(CodeStyleOptions.PreferReadonly, field.Language);
        }

        private static bool IsFieldWrite(IFieldReferenceOperation fieldReference, ISymbol owningSymbol)
        {
            // Field writes: assignment, increment/decrement or field passed by ref.
            var isFieldAssignemnt = fieldReference.Parent is IAssignmentOperation assignmentOperation &&
                assignmentOperation.Target == fieldReference;
            if (isFieldAssignemnt ||
                fieldReference.Parent is IIncrementOrDecrementOperation ||
                fieldReference.Parent is IArgumentOperation argumentOperation && argumentOperation.Parameter.RefKind != RefKind.None ||
                IsInLeftOfDeconstructionAssignment(fieldReference))
            {
                // Writes to fields inside constructor are ignored, except for the below cases:
                //  1. Instance reference of an instance field being written is not the instance being initialized by the constructor.
                //  2. Field is being written inside a lambda or local function.

                // Check if we are in the constructor of the containing type of the written field.
                var isInConstructor = owningSymbol.IsConstructor();
                var isInStaticConstructor = owningSymbol.IsStaticConstructor();
                var field = fieldReference.Field;
                if ((isInConstructor || isInStaticConstructor) &&
                    field.ContainingType == owningSymbol.ContainingType)
                {
                    // For instance fields, ensure that the instance reference is being initialized by the constructor.
                    var instanceFieldWrittenInCtor = isInConstructor &&
                        fieldReference.Instance?.Kind == OperationKind.InstanceReference &&
                        (!isFieldAssignemnt || fieldReference.Parent.Parent?.Kind != OperationKind.ObjectOrCollectionInitializer);

                    // For static fields, ensure that we are in the static constructor.
                    var staticFieldWrittenInStaticCtor = isInStaticConstructor && field.IsStatic;

                    if (instanceFieldWrittenInCtor || staticFieldWrittenInStaticCtor)
                    {
                        // Finally, ensure that the write is not inside a lambda or local function.
                        if (!IsInAnonymousFunctionOrLocalFunction(fieldReference))
                        {
                            // It is safe to ignore this write.
                            return false;
                        }
                    }
                }

                return true;
            }

            return false;
        }

        private static bool IsInAnonymousFunctionOrLocalFunction(IOperation operation)
        {
            operation = operation.Parent;
            while (operation != null)
            {
                switch (operation.Kind)
                {
                    case OperationKind.AnonymousFunction:
                    case OperationKind.LocalFunction:
                        return true;
                }

                operation = operation.Parent;
            }

            return false;
        }

        private static bool IsInLeftOfDeconstructionAssignment(IOperation operation)
        {
            var previousOperation = operation;
            operation = operation.Parent;

            while (operation != null)
            {
                switch (operation.Kind)
                {
                    case OperationKind.DeconstructionAssignment:
                        var deconstructionAssignment = (IDeconstructionAssignmentOperation)operation;
                        return deconstructionAssignment.Target == previousOperation;

                    case OperationKind.Tuple:
                    case OperationKind.Conversion:
                    case OperationKind.Parenthesized:
                        previousOperation = operation;
                        operation = operation.Parent;
                        continue;

                    default:
                        return false;
                }
            }

            return false;
        }

        private static bool IsMutableValueType(ITypeSymbol type)
        {
            if (type.TypeKind != TypeKind.Struct)
            {
                return false;
            }

            foreach (var member in type.GetMembers())
            {
                if (member is IFieldSymbol fieldSymbol &&
                    !(fieldSymbol.IsConst || fieldSymbol.IsReadOnly || fieldSymbol.IsStatic))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
