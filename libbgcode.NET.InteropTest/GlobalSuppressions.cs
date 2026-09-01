using System.Diagnostics.CodeAnalysis;

[assembly: SuppressMessage("StyleCop.CSharp.NamingRules",
                           "SA1300:Element should begin with upper-case letter",
                           Justification = "The namespace matches the package id, which follows the upstream library's lowercase name.",
                           Scope = "namespaceanddescendants",
                           Target = "~N:libbgcode.NET.InteropTest")]
