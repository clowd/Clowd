// Copyright (c) Rudy Huyn. All rights reserved.
// Licensed under the MIT License.
// Source: https://github.com/DotNetPlus/ReswPlus

namespace Clowd.Localization.Providers;

internal class SlovenianProvider : IPluralProvider
{
    public PluralTypeEnum ComputePlural(double n)
    {
        var isInt = n.IsInt();
        if (isInt)
        {
            switch ((int)n)
            {
                case 1:
                    return PluralTypeEnum.ONE;
                case 2:
                    return PluralTypeEnum.TWO;
                case 3:
                case 4:
                    return PluralTypeEnum.TWO;
            }

            return PluralTypeEnum.OTHER;
        }
        else
        {
            return PluralTypeEnum.FEW;
        }
    }
}
