﻿// Copyright (c) Rudy Huyn. All rights reserved.
// Licensed under the MIT License.
// Source: https://github.com/DotNetPlus/ReswPlus

namespace Clowd.Localization.Providers;

internal class RomanianProvider : IPluralProvider
{
    public PluralTypeEnum ComputePlural(double n)
    {
        if (n.GetNumberOfDigitsAfterDecimal() > 0 || n == 0 || (n != 1 && (n % 100).IsBetween(1, 19)))
        {
            return PluralTypeEnum.FEW;
        }
        if (n == 1)
        {
            return PluralTypeEnum.ONE;
        }
        return PluralTypeEnum.OTHER;

    }
}
