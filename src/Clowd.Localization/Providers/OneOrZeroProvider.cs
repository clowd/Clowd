// Copyright (c) Rudy Huyn. All rights reserved.
// Licensed under the MIT License.
// Source: https://github.com/DotNetPlus/ReswPlus

namespace Clowd.Localization.Providers;

internal class OneOrZeroProvider : IPluralProvider
{
    public PluralTypeEnum ComputePlural(double n)
    {
        if (n == 0)
            return PluralTypeEnum.ZERO;
        if (n == 1)
        {
            return PluralTypeEnum.ONE;
        }
        else
        {
            return PluralTypeEnum.OTHER;
        }
    }

}
