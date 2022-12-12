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
using System.Windows.Shapes;

namespace OpenVRChatHapticFeedback
{
	/// <summary>
	/// Логика взаимодействия для WhiteListWindow.xaml
	/// </summary>
	public partial class WhiteListWindow : Window
	{
		public List<string> Nicks = new List<string>();
		public bool IsEnglish = false;
		public WhiteListWindow()
		{
			InitializeComponent();
		}

		private void BuAddNick_Click(object sender, RoutedEventArgs e)
		{
			if (!Nicks.Contains(TbNew.Text))
			{
				Nicks.Add(TbNew.Text);
				LbNicks.Items.Add(TbNew.Text);
			}
		}

		private void Window_Loaded(object sender, RoutedEventArgs e)
		{
			LbNicks.Items.Clear();
			foreach(var nick in Nicks)
				LbNicks.Items.Add(nick);

			if (IsEnglish)
			{
				BuDelete.Content = "Delete";
				BuAddNick.Content = "Add";
			}
		}

		private void BuDelete_Click(object sender, RoutedEventArgs e)
		{
			if (LbNicks.SelectedIndex == -1)
				return;
			string name = (string)LbNicks.Items[LbNicks.SelectedIndex];
			LbNicks.Items.RemoveAt(LbNicks.SelectedIndex);
			Nicks.Remove(name);
		}
	}
}
