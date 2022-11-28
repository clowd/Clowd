// Copyright (c) Rudy Huyn. All rights reserved.
// Licensed under the MIT License.
// Source: https://github.com/DotNetPlus/ReswPlus

namespace Clowd.Localization.Providers;

internal class ZeroToOneProvider : IPluralProvider
{
    public PluralTypeEnum ComputePlural(double n)
    {
        if (n.IsBetween(0, 1))
        {
            return PluralTypeEnum.ONE;
        }
        return PluralTypeEnum.OTHER;

    }
}
