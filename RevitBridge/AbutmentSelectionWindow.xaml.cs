using RevitBridgeAddin;
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
using Autodesk.Revit.UI;
using Autodesk.Revit.DB;
using VDS.RDF;
using System.Windows;

namespace RevitBridge
{
    public partial class AbutmentSelectionWindow : Window
    {
        public bool IsA1Selected { get; private set; }
        public bool IsA2Selected { get; private set; }

        private ExternalCommandData _commandData;
        private string _ttlFilePath;
        private string _message;
        private ElementSet _elements;

        public AbutmentSelectionWindow(ExternalCommandData commandData, string ttlFilePath, ref string message, ElementSet elements)
        {
            InitializeComponent();
            _commandData = commandData;
            _ttlFilePath = ttlFilePath;
            _message = message;
            _elements = elements;
        }

        private void SelectA1_Click(object sender, RoutedEventArgs e)
        {
            IsA1Selected = true;
            IsA2Selected = false;
            this.DialogResult = true;
            this.Close();
        }

        private void SelectA2_Click(object sender, RoutedEventArgs e)
        {
            IsA1Selected = false;
            IsA2Selected = true;
            this.DialogResult = true;
            this.Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = false;
            this.Close();
        }
    }
}