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
    /// Interaction logic for AddClientGrDialog.xaml
    /// </summary>
    public partial class AddClientGrDialog : Window
    {
        private Processor mamProcessor = null;
        private Group selGroup = null;

        public AddClientGrDialog(Processor processor, Group addedToGroup)
        {
            mamProcessor = processor;
            selGroup = addedToGroup;
            InitializeComponent();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            txtLogin.Focus();
            txtGroupName.Content = selGroup.Name;
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
                    if (selGroup.Name.Equals(_user.Group, StringComparison.InvariantCultureIgnoreCase))
                    {
                        btnAdd.IsEnabled = false;
                    }
                    else
                    {
                        btnAdd.IsEnabled = true;
                    }
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
            if (mamProcessor.AppendClientGr(selGroup.Name, clientLogin, clientName, clientMultiplier))
                this.DialogResult = true;
            this.Close();
        }

        private void btnCancel_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }
    }
}
