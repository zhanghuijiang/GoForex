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
    /// Interaction logic for EditClientDialog.xaml
    /// </summary>
    public partial class EditClientDialog : Window
    {
        private Manager selectedManager = null;
        private Client selectedClient = null;
        private Processor processor = null;
        public EditClientDialog(Processor _processor,Manager manager,Client client)
        {
            processor = _processor;
            selectedClient = client;
            selectedManager = manager;
            InitializeComponent();
        }

        private void btnUpdate_Click(object sender, RoutedEventArgs e)
        {
            if(txtMultiplier.Text == string.Empty)
            {
                txtMultiplier.Focus();
                return;
            }
            double newMultiplier = Convert.ToDouble(txtMultiplier.Text);

            if (processor.UpdateClient(selectedManager.Login, selectedClient.Login, newMultiplier))
                this.DialogResult = true;
            this.Close();
        }

        private void btnCancel_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = false;
            this.Close();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            if (selectedClient != null)
            {
                txtLogin.Content = selectedClient.Login.ToString();
                txtUserName.Content = selectedClient.Name;
                txtMultiplier.Text = selectedClient.Multiplier.ToString();
            }

            txtMultiplier.Focus();
        }
    }
}
