﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Clowd.Controls
{
    public class SizeAwareProgressBar : ProgressBar
    {
        public string CurrentSizeDisplay
        {
            get { return (string)GetValue(CurrentSizeDisplayProperty); }
            set { SetValue(CurrentSizeDisplayProperty, value); }
        }

        // Using a DependencyProperty as the backing store for CurrentSizeDisplay.  This enables animation, styling, binding, etc...
        public static readonly DependencyProperty CurrentSizeDisplayProperty =
            DependencyProperty.Register("CurrentSizeDisplay", typeof(string), typeof(SizeAwareProgressBar), new PropertyMetadata(""));


        public SizeAwareProgressBar()
        {

        }

    }
}
