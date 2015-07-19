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
    /// Interaction logic for RemoveClientGrDialog.xaml
    /// </summary>
    public partial class RemoveClientGrDialog : Window
    {
        Client selectedClient = null;
        Group selectedGroup = null;
        public RemoveClientGrDialog(Client targetClient, Group targetGroup)
        {
            selectedClient = targetClient;
            selectedGroup = targetGroup;
            InitializeComponent();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            txtClientName.Content = selectedClient.Name;
            txtLogin.Content = selectedClient.Login.ToString();
            txtGroupName.Content = selectedGroup.Name;
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
