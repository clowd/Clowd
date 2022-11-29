﻿// Copyright (c) Rudy Huyn. All rights reserved.
// Licensed under the MIT License.
// Source: https://github.com/DotNetPlus/ReswPlus

namespace Clowd.Localization.Providers;

internal class MacedonianProvider : IPluralProvider
{
    public PluralTypeEnum ComputePlural(double n)
    {
        if (n.IsInt())
        {
            if (n % 10 == 1)
            {
                return PluralTypeEnum.ONE;
            }
        }
        else
        {
            var f = n.DigitsAfterDecimal();
            if (f % 10 == 1)
            {
                return PluralTypeEnum.ONE;
            }
        }
        return PluralTypeEnum.OTHER;
    }
}
