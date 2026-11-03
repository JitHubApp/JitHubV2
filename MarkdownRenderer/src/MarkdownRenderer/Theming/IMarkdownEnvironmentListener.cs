using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.System;
using Microsoft.UI.Xaml;
using Windows.UI;
using Windows.UI.ViewManagement;
using MarkdownRenderer.Diagnostics;

namespace MarkdownRenderer.Theming;

/// <summary>Receives shared window-environment changes without being rooted by the monitor.</summary>
internal interface IMarkdownEnvironmentListener
{
    void OnMarkdownEnvironmentChanged(
        in MarkdownEnvironmentSnapshot snapshot,
        MarkdownEnvironmentChange changes);
}
