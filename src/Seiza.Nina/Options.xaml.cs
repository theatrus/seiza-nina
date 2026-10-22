using System.ComponentModel.Composition;
using System.Windows;

namespace Seiza.Nina;

[Export(typeof(ResourceDictionary))]
public partial class Options : ResourceDictionary
{
    public Options() => InitializeComponent();
}
