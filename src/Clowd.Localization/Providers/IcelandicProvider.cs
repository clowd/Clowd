// Copyright (c) Rudy Huyn. All rights reserved.
// Licensed under the MIT License.
// Source: https://github.com/DotNetPlus/ReswPlus

namespace Clowd.Localization.Providers;

internal class IcelandicProvider : IPluralProvider
{
    public PluralTypeEnum ComputePlural(double n)
    {
        if (n.IsInt())
        {
            var integer = (int)n;
            if (integer % 10 == 1 && integer % 100 != 11)
            {
                return PluralTypeEnum.ONE;
            }
            return PluralTypeEnum.OTHER;
        }
        else
        {
            return PluralTypeEnum.ONE;
        }


    }
}
