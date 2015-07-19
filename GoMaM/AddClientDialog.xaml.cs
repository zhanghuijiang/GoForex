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
    /// Interaction logic for AddClientDialog.xaml
    /// </summary>
    public partial class AddClientDialog : Window
    {
        private Processor mamProcessor = null;
        private Manager selManager = null;

        public AddClientDialog(Processor processor,Manager addedToManager)
        {
            mamProcessor = processor;
            selManager = addedToManager;
            InitializeComponent();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            txtLogin.Focus();
            txtManagerLogin.Content = selManager.Login.ToString();
            txtManagerName.Content = selManager.Name;
            var _user = mamProcessor.GetMTUser(selManager.Login);
            txtManagerGroup.Content = (_user == null ? "" : _user.Group);
        }

        private void btnSearch_Click(object sender, RoutedEventArgs e)
        {
            int inputNum;
            if (int.TryParse(txtLogin.Text, out inputNum))
            {
                /*
                string name = mamProcessor.GetMTUserName(inputNum);
                txtUserName.Content = name;
                if (name != Constants.USER_NOT_FOUND)
                    btnAdd.IsEnabled = true;
                else
                {
                    txtLogin.Focus();
                    btnAdd.IsEnabled = false;
                }
                */
                var _user = mamProcessor.GetMTUser(inputNum);
                if (_user == null)
                {
                    txtUserName.Content = Constants.USER_NOT_FOUND;
                    txtUserGroup.Content = "";
                    txtLogin.Focus();
                    btnAdd.IsEnabled = false;
                }
                else
                {
                    txtUserName.Content = _user.Name;
                    txtUserGroup.Content = _user.Group;
                    btnAdd.IsEnabled = true;
                }
            }
        }

        private void btnAdd_Click(object sender, RoutedEventArgs e)
        {
            if (txtLogin.Text == string.Empty)
            {
                txtLogin.Focus();
                return;
            }

            if (txtMultiplier.Text == string.Empty)
            {
                txtMultiplier.Focus();
                return;
            }

            string clientName = txtUserName.Content.ToString();
            int clientLogin = Convert.ToInt32(txtLogin.Text);
            double clientMultiplier = Convert.ToDouble(txtMultiplier.Text);
            if (mamProcessor.AppendClient(selManager.Login, clientLogin, clientName, clientMultiplier))
                this.DialogResult = true;
            this.Close();
        }

        private void btnCancel_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }
    }
}
