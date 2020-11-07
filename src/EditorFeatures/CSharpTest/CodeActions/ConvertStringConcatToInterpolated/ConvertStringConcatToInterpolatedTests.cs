﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.CodeRefactorings.ConvertStringConcatToInterpolated;
using Microsoft.CodeAnalysis.Editor.UnitTests.CodeActions;
using Microsoft.CodeAnalysis.Test.Utilities;
using Microsoft.CodeAnalysis.Testing;
using Xunit;

namespace Microsoft.CodeAnalysis.Editor.CSharp.UnitTests.CodeActions.ConvertStringConcatToInterpolated
{
    using VerifyCS = CSharpCodeRefactoringVerifier<CSharpConvertStringConcatToInterpolatedRefactoringProvider>;

    public class ConvertStringConcatToInterpolatedTests
    {
        [Fact, Trait(Traits.Feature, Traits.Features.CodeActionsConvertStringConcatToInterpolated)]
        public async Task IfExpressionAndConcatenatedTextAreRefactoredToInterpolated()
        {
            const string InitialMarkup = @"
class Program
{
    public static void Main()
    {
        var x = (true ? ""t"" : ""f"") [|+|] ""a"";
    }
}";
            const string ExpectedMarkup = @"
class Program
{
    public static void Main()
    {
        var x = $""{(true ? ""t"" : ""f"")}a"";
    }
}";
            await new VerifyCS.Test
            {
                TestCode = InitialMarkup,
                FixedCode = ExpectedMarkup,
                CodeActionValidationMode = CodeActionValidationMode.SemanticStructure,
            }.RunAsync();
        }

        [Fact, Trait(Traits.Feature, Traits.Features.CodeActionsConvertStringConcatToInterpolated)]
        public async Task IfExpressionSurroundedByConcatenatedTextAreRefactoredToInterpolated()
        {
            const string InitialMarkup = @"
class Program
{
    public static void Main()
    {
        var x = ""a"" + (true ? ""t"" : ""f"") [|+|] ""b"";
    }
}";
            const string ExpectedMarkup = @"
class Program
{
    public static void Main()
    {
        var x = $""a{(true ? ""t"" : ""f"")}b"";
    }
}";
            await new VerifyCS.Test
            {
                TestCode = InitialMarkup,
                FixedCode = ExpectedMarkup,
                CodeActionValidationMode = CodeActionValidationMode.SemanticStructure,
            }.RunAsync();
        }

        [Fact, Trait(Traits.Feature, Traits.Features.CodeActionsConvertStringConcatToInterpolated)]
        public async Task TwoIfExpressionSurroundedByConcatenatedTextAreRefactoredToInterpolated()
        {
            const string InitialMarkup = @"
class Program
{
    public static void Main()
    {
        var x = ""a"" + (true ? ""t"" : ""f"") [|+|] ""b"" + (false ? ""t"" : ""f"") + ""c"";
    }
}";
            const string ExpectedMarkup = @"
class Program
{
    public static void Main()
    {
        var x = $""a{(true ? ""t"" : ""f"")}b{(false ? ""t"" : ""f"")}c"";
    }
}";
            await new VerifyCS.Test
            {
                TestCode = InitialMarkup,
                FixedCode = ExpectedMarkup,
                CodeActionValidationMode = CodeActionValidationMode.SemanticStructure,
            }.RunAsync();
        }

        [Theory, Trait(Traits.Feature, Traits.Features.CodeActionsConvertStringConcatToInterpolated)]
        [InlineData(@"""a"" [|+|] ""b""")]
        [InlineData(@"""a"" [|+|] @""b""")]
        [InlineData(@"""a"" [|+|] @""b"" + ""c""")]
        public async Task DontOfferIfOnlyStringLiteralsAreConcatenated(string concatenations)
        {
            var initialMarkup = @$"
class Program
{{
    public static void Main()
    {{
        var x = {concatenations};
    }}
}}";
            await new VerifyCS.Test
            {
                TestCode = initialMarkup,
                FixedCode = initialMarkup,
                OffersEmptyRefactoring = false,
            }.RunAsync();
        }
    }
}
