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
    /// Interaction logic for AddManagerDialog.xaml
    /// </summary>
    public partial class RefreshMultipleDialog : Window
    {
        private Processor mamProcessor;
        private Manager selectedManager = null;
        public RefreshMultipleDialog(Processor processor, Manager manager)
        {
            mamProcessor = processor;
            selectedManager = manager;
            InitializeComponent();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            if (selectedManager != null)
            {
                txtLogin.Text = selectedManager.Login.ToString();
                txtUserName.Content = selectedManager.Name;
                txtGroups.Text = selectedManager.Groups;
                txtMinBalance.Text = (selectedManager.MinBalance.HasValue ? selectedManager.MinBalance.Value.ToString() : "");
            }

            txtLogin.Focus();
        }

        private void btnSearch_Click(object sender, RoutedEventArgs e)
        {
            int inputNum;
            if (int.TryParse(txtLogin.Text, out inputNum))
            {
                string name = mamProcessor.GetMTUserName(inputNum);
                txtUserName.Content = name;
                if (name != Constants.USER_NOT_FOUND)
                {
                    //btnAdd.IsEnabled = true;
                }
                else
                {
                    txtLogin.Focus();
                    //btnAdd.IsEnabled = false;
                }
            }
        }

        private void btnAdd_Click(object sender, RoutedEventArgs e)
        {
            if (txtUserName.Content != null && txtUserName.Content.ToString() != string.Empty && txtLogin.Text.ToString() != string.Empty && txtGroups.Text.ToString() != string.Empty && txtMinBalance.Text.ToString() != string.Empty)
            {
                if (mamProcessor.RefreshMultiple(Convert.ToInt32(txtLogin.Text), txtUserName.Content.ToString(), "", txtGroups.Text, Convert.ToInt32(txtMinBalance.Text)))
                {
                    MessageBox.Show("Done successfully");
                    this.DialogResult = true;
                }
                this.Close();
            }
            else
            {
                MessageBox.Show("Some data is missing");
            }
        }

        private void btnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            this.Close();
        }
    }
}
