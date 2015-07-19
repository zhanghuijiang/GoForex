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
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Text.RegularExpressions;

namespace GoMaM
{
    public class NumericTextBox : TextBox
    {
        public NumericTextBox()
        {
            //DefaultStyleKeyProperty.OverrideMetadata(typeof(NumericTextBox), new FrameworkPropertyMetadata(typeof(NumericTextBox)));
        }

        protected override void OnPreviewTextInput(TextCompositionEventArgs e)
        {
            try
            {
                var _text = ((TextBox)e.Source).Text;
                if (e.Text[0] == '-')
                {
                    if (Unsigned || ((TextBox)e.Source).CaretIndex > 0 || _text.Contains("-"))
                        e.Handled = true;
                }
                else if (e.Text[0] == '.')
                {
                    if (!SupportDecimal || _text.Length == 0 || _text.Contains(".") || !Char.IsDigit(_text, _text.Length - 1))
                        e.Handled = true;
                }
                else
                {
                    if (!Char.IsDigit(e.Text, 0))
                        e.Handled = true;
                }
            }
            catch
            {
                e.Handled = true;
            }
            base.OnPreviewTextInput(e);
        }

        public bool SupportDecimal { get; set; }

        public bool Unsigned { get; set; }

//        public static DependencyProperty SupportDecimalProperty = DependencyProperty.Register("SypportDecimal", typeof(bool), typeof(NumericTextBox), new PropertyMetadata(string.Empty));

        
        /*public bool SypportDecimal
        {
            get 
            {
                return (bool)GetValue(SupportDecimalProperty); 
            }
            set
            {
                SetValue(SupportDecimalProperty, value); 
            }
        }*/
    }
}
