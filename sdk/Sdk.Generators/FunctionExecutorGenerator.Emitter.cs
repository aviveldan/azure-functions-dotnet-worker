﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System.Collections.Generic;
using System.Text;
using System.Threading;

internal partial class FunctionExecutorGenerator
{
    internal class Emitter
    {
        public string Emit(IEnumerable<FuncInfo> functions, CancellationToken cancellationToken)
        {
            string result = $$"""
                         // <auto-generated/>
                         using System;
                         using System.Threading.Tasks;
                         using Microsoft.Extensions.DependencyInjection;
                         using Microsoft.Azure.Functions.Worker.Context.Features;
                         using Microsoft.Azure.Functions.Worker.Invocation;
                         namespace Microsoft.Azure.Functions.Worker
                         {
                             internal class DirectFunctionExecutor : IFunctionExecutor
                             {
                                 public async Task ExecuteAsync(FunctionContext context)
                                 {
                                     {{GetMethodsContent(functions)}}
                                 }
                             }
                         }
                         """;

            return result;
        }

        private string GetMethodsContent(IEnumerable<FuncInfo> functions)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(@"var modelBindingFeature = context.Features.Get<IModelBindingFeature>()!;
            var inputArguments = await modelBindingFeature.BindFunctionInputAsync(context)!;");
            foreach (FuncInfo function in functions)
            {
                sb.Append($@"
            if (string.Equals(context.FunctionDefinition.Name, ""{function.FunctionName}"", StringComparison.OrdinalIgnoreCase))
            {{");

                int paramCounter = 0;
                var paramInputs = new List<string>();
                foreach (var argumentTypeName in function.ParentClass.ConstructorArgumentTypeNames)
                {
                    paramCounter++;
                    sb.Append($@"
                var p{paramCounter} = context.InstanceServices.GetService<{argumentTypeName}>();");
                    paramInputs.Add($"p{paramCounter}");
                }
                var inputs = string.Join(", ", paramInputs);

                int paramCounter2 = 0;
                var paramInputs2 = new List<string>();
                foreach (var argumentTypeName in function.ParameterTypeNames)
                {
                    paramCounter2++;
                    paramInputs2.Add($"({argumentTypeName})inputArguments[{paramCounter2}]");
                }
                var methodInputs = string.Join(", ", paramInputs2);

                sb.Append(@"
                ");
                if (function.IsReturnValueAssignable)
                {
                    sb.Append(@$"context.GetInvocationResult().Value = ");
                }
                if (function.ShouldAwait)
                {
                    sb.Append("await ");
                }
                if (function.IsStatic)
                {
                    sb.Append(@$"{function.ParentClass.ClassName}.{function.MethodName}({methodInputs});
            }}");
                }
                else
                {
                    sb.Append($@"new {function.ParentClass.ClassName}({inputs}).{function.MethodName}({methodInputs});
            }}");
                }


            }

            return sb.ToString();

        }

        private string GetMethodContent(FuncInfo function)
        {
            var sb = new StringBuilder();
            int paramCounter = 0;
            var paramInputs = new List<string>();
            foreach (var argumentTypeName in function.ParentClass.ConstructorArgumentTypeNames)
            {
                paramCounter++;
                sb.AppendLine($@"
                var p{paramCounter} = context.InstanceServices.GetService<{argumentTypeName}>();");
                paramInputs.Add($"p{paramCounter}");
            }

            return sb.ToString();
        }
    }
}
