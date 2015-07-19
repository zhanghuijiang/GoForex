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
    /// Interaction logic for RemoveClientDialog.xaml
    /// </summary>
    public partial class RemoveClientDialog : Window
    {
        Client selectedClient = null;
        Manager selectedManager = null;
        public RemoveClientDialog(Client targetClient,Manager targetManager)
        {
            selectedClient = targetClient;
            selectedManager = targetManager;
            InitializeComponent();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            txtClientName.Content = selectedClient.Name;
            txtLogin.Content = selectedClient.Login.ToString();
            txtManagerName.Content = selectedManager.Name;
            txtManLogin.Content = selectedManager.Login.ToString();
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
