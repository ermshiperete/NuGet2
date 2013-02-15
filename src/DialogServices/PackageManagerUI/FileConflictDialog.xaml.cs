﻿using System;
using System.Windows.Controls;
using System.Windows.Interop;
using Microsoft.VisualStudio.PlatformUI;

namespace NuGet.Dialog.PackageManagerUI
{
    /// <summary>
    /// Interaction logic for FileConflictDialog.xaml
    /// </summary>
    public partial class FileConflictDialog : DialogWindow
    {
        public FileConflictDialog()
        {
            InitializeComponent();
        }

        protected override void OnSourceInitialized(System.EventArgs e)
        {
            base.OnSourceInitialized(e);

            IntPtr hMenu = NativeMethods.GetSystemMenu(new WindowInteropHelper(this).Handle, false);
            int menuItemCount = NativeMethods.GetMenuItemCount(hMenu);
            NativeMethods.RemoveMenu(hMenu, menuItemCount - 1, NativeMethods.MF_BYPOSITION);
        }

        public string Question
        {
            get
            {
                return QuestionText.Text;
            }
            set
            {
                QuestionText.Text = value;
            }
        }

        public FileConflictResolution UserSelection
        {
            get;
            private set;
        }

        private void OnButtonClick(object sender, System.Windows.RoutedEventArgs e)
        {
            var button = (Button)sender;
            string tagValue = (string)button.Tag;

            UserSelection = (FileConflictResolution)Enum.Parse(typeof(FileConflictResolution), tagValue);

            DialogResult = true;
        }
    }
}