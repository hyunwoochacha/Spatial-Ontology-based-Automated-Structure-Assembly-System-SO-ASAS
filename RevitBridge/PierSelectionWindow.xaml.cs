using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

using System.Windows;

namespace RevitBridge
{
    public partial class PierSelectionWindow : Window
    {
        // Selected pier name
        public string SelectedPierName { get; private set; }

        // Constructor that accepts a list of pier names
        public PierSelectionWindow(List<string> pierNames)
        {
            InitializeComponent();

            // Add pier names to ComboBox
            foreach (var pierName in pierNames)
            {
                PierComboBox.Items.Add(pierName);
            }
        }

        // OK button click handler
        private void Confirm_Click(object sender, RoutedEventArgs e)
        {
            // Save selected pier name and close window
            SelectedPierName = PierComboBox.SelectedItem as string;
            DialogResult = true;
            Close();
        }

        // Cancel button click handler
        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
