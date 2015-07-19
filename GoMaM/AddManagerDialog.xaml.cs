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
    public partial class AddManagerDialog : Window
    {
        private Processor mamProcessor;
        public AddManagerDialog(Processor processor)
        {
            mamProcessor = processor;
            InitializeComponent();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
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
                    btnAdd.IsEnabled = true;
                else
                {
                    txtLogin.Focus();
                    btnAdd.IsEnabled = false;
                }
            }
        }

        private void btnAdd_Click(object sender, RoutedEventArgs e)
        {
            if (txtUserName.Content.ToString() != string.Empty)
            {
                if (mamProcessor.AppendManager(Convert.ToInt32(txtLogin.Text), txtUserName.Content.ToString(), ""))
                    this.DialogResult = true;
            }

            this.Close();
        }

        private void btnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            this.Close();
        }
    }
}
