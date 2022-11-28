// Copyright (c) Rudy Huyn. All rights reserved.
// Licensed under the MIT License.
// Source: https://github.com/DotNetPlus/ReswPlus

namespace Clowd.Localization.Providers;

internal class OtherProvider : IPluralProvider
{
    public PluralTypeEnum ComputePlural(double n)
    {
        return PluralTypeEnum.OTHER;
    }
}
