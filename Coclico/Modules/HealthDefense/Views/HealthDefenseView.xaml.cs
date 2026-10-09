using System.Windows.Controls;
using Coclico.Services;
using Coclico.ViewModels;

namespace Coclico.Views;

public partial class HealthDefenseView : UserControl
{
    public HealthDefenseView()
    {
        InitializeComponent();
        SystemHealthService healthService = ServiceContainer.GetRequired<SystemHealthService>();
        var vm = new HealthDefenseViewModel(healthService);
        DataContext = vm;

        vm.OutputLog.CollectionChanged += (_, e) =>
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add && ConsoleListBox.Items.Count > 0)
            {
                _ = Dispatcher.InvokeAsync(() =>
                {
                    if (ConsoleListBox.Items.Count > 0)
                    {
                        ConsoleListBox.ScrollIntoView(ConsoleListBox.Items[^1]);
                    }
                }, System.Windows.Threading.DispatcherPriority.Background);
            }
        };
    }
}

