// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 Henrik O. Sørensen
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0. If a copy of
// the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Diagnostics.CodeAnalysis;

[assembly: SuppressMessage("StyleCop.CSharp.NamingRules",
                           "SA1300:Element should begin with upper-case letter",
                           Justification = "The namespace matches the package id, which follows the upstream library's lowercase name.",
                           Scope = "namespaceanddescendants",
                           Target = "~N:libbgcode.NET.Fuzz")]
