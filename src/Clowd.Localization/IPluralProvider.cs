// Copyright (c) Rudy Huyn. All rights reserved.
// Licensed under the MIT License.
// Source: https://github.com/DotNetPlus/ReswPlus

namespace Clowd.Localization;

internal interface IPluralProvider
{
    PluralTypeEnum ComputePlural(double n);
}
