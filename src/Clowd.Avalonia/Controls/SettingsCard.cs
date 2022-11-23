using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using DependencyPropertyGenerator;
using FluentAvalonia.UI.Controls;

namespace Clowd.Avalonia.Controls;

[DependencyProperty<FAIconElement>("Icon")]
[DependencyProperty<string>("Title")]
[DependencyProperty<string>("Description")]
[DependencyProperty<object>("ActionContent")]
[DependencyProperty<object>("BottomContent")]
public partial class SettingsCard : TemplatedControl
{
}

[DependencyProperty<FAIconElement>("Icon")]
[DependencyProperty<string>("Title")]
[DependencyProperty<string>("Description")]
public partial class ExpandingSettingsCard : ContentControl
{
}
