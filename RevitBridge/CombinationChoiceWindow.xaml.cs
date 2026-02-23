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

using System.Windows;

namespace RevitBridge
{
    public partial class CombinationChoiceWindow : Window
    {
        public bool IsFullCombination { get; private set; }

        private ExternalCommandData _commandData;
        private string _ttlFilePath;
        private string _message;
        private ElementSet _elements;

        public CombinationChoiceWindow(ExternalCommandData commandData, string ttlFilePath, ref string message, ElementSet elements)
        {
            InitializeComponent();
            _commandData = commandData;
            _ttlFilePath = ttlFilePath;
            _message = message;
            _elements = elements;
        }

        private void FullCombinationButton_Click(object sender, RoutedEventArgs e)
        {
            IsFullCombination = true;
            this.DialogResult = true;
            this.Close();
        }

        private void PartialCombinationButton_Click(object sender, RoutedEventArgs e)
        {
            IsFullCombination = false;
            this.DialogResult = true;
            this.Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = false;
            this.Close();
        }
    }
}