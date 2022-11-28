// Copyright (c) Rudy Huyn. All rights reserved.
// Licensed under the MIT License.
// Source: https://github.com/DotNetPlus/ReswPlus

namespace Clowd.Localization.Providers;

internal class ScottishGaelicProvider : IPluralProvider
{
    public PluralTypeEnum ComputePlural(double n)
    {
        if (n.IsInt())
        {
            var i = (int)n;
            switch (i)
            {
                case 1:
                case 11:
                    return PluralTypeEnum.ONE;
                case 2:
                case 12:
                    return PluralTypeEnum.TWO;
            }
            if (i <= 19)
                return PluralTypeEnum.FEW;
        }
        return PluralTypeEnum.OTHER;
    }
}
