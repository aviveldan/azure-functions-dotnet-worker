﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System.Collections.Generic;
using System.Text;
using System.Threading;

internal partial class FunctionExecutorGenerator
{
    internal static class Emitter
    {
        internal static string Emit(IEnumerable<ExecutableFunction> functions, CancellationToken cancellationToken)
        {
            string result = $$"""
                         // <auto-generated/>
                         using System;
                         using System.Threading.Tasks;
                         using Microsoft.Extensions.Hosting;
                         using Microsoft.Extensions.DependencyInjection;
                         using Microsoft.Azure.Functions.Worker.Context.Features;
                         using Microsoft.Azure.Functions.Worker.Invocation;
                         namespace Microsoft.Azure.Functions.Worker
                         {
                             internal class DirectFunctionExecutor : IFunctionExecutor
                             {
                                 public async Task ExecuteAsync(FunctionContext context)
                                 {
                                     {{GetMethodBody(functions)}}
                                 }
                             }
                             public static class FunctionExecutorHostBuilderExtensions
                             {
                                 ///<summary>
                                 /// Configures an optimized function executor to the invocation pipeline.
                                 ///</summary>
                                 public static IHostBuilder ConfigureGeneratedFunctionExecutor(this IHostBuilder builder)
                                 {
                                     return builder.ConfigureServices(s => 
                                     {
                                         s.AddSingleton<IFunctionExecutor, DirectFunctionExecutor>();
                                     });
                                 }
                             }
                         }
                         """;

            return result;
        }

        private static string GetMethodBody(IEnumerable<ExecutableFunction> functions)
        {
            var sb = new StringBuilder();
            
            sb.Append(@"var modelBindingFeature = context.Features.Get<IModelBindingFeature>()!;
            var inputArguments = await modelBindingFeature.BindFunctionInputAsync(context)!;");
            foreach (ExecutableFunction function in functions)
            {
                sb.Append($@"
            if (string.Equals(context.FunctionDefinition.Name, ""{function.EntryPoint}"", StringComparison.OrdinalIgnoreCase))
            {{");

                int paramCounter = 0;
                var constructorParamTypeNameList = new List<string>();
                foreach (var argumentTypeName in function.ParentFunctionClass.ConstructorParameterTypeNames)
                {
                    paramCounter++;
                    sb.Append($@"
                var p{paramCounter} = context.InstanceServices.GetService<{argumentTypeName}>();");
                    constructorParamTypeNameList.Add($"p{paramCounter}");
                }
                var constructorParamsStr = string.Join(", ", constructorParamTypeNameList);

                int paramCounter2 = 0;
                var functionParamList = new List<string>();
                foreach (var argumentTypeName in function.ParameterTypeNames)
                {
                    paramCounter2++;
                    functionParamList.Add($"({argumentTypeName})inputArguments[{paramCounter2}]");
                }
                var methodParamsStr = string.Join(", ", functionParamList);

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

                sb.Append(function.IsStatic
                    ? @$"{function.ParentFunctionClass.ClassName}.{function.MethodName}({methodParamsStr});
            }}"
                    : $@"new {function.ParentFunctionClass.ClassName}({constructorParamsStr}).{function.MethodName}({methodParamsStr});
            }}");
            }

            return sb.ToString();
        }
    }
}
