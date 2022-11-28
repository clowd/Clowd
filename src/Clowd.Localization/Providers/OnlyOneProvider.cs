// Copyright (c) Rudy Huyn. All rights reserved.
// Licensed under the MIT License.
// Source: https://github.com/DotNetPlus/ReswPlus

namespace Clowd.Localization.Providers;

internal class OnlyOneProvider : IPluralProvider
{
    public PluralTypeEnum ComputePlural(double n)
    {
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
