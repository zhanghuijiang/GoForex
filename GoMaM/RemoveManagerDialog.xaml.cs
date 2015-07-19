using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using MAM;
namespace GoMaM
{
    /// <summary>
    /// Interaction logic for RemoveManagerDialog.xaml
    /// </summary>
    public partial class RemoveManagerDialog : Window
    {
        private Manager selectedManager = null;
        public RemoveManagerDialog(Manager manager)
        {
            selectedManager = manager;
            InitializeComponent();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            txtLogin.Content = selectedManager.Login.ToString();
            txtUserName.Content = selectedManager.Name;
        }

        private void btnRemove_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = true;
            this.Close();
        }

        private void btnCancel_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = false;
            this.Close();
        }
    }
}
