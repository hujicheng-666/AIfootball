using System;
using System.Runtime.InteropServices;

namespace Assets.SuperGoalie.Scripts.Trajectories
{
    public static class WindowsCsvFileDialog
    {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        const int MaxPathLength = 4096;
        const int OfnPathMustExist = 0x00000800;
        const int OfnFileMustExist = 0x00001000;
        const int OfnExplorer = 0x00080000;
        const int OfnNoChangeDir = 0x00000008;

        [StructLayout(LayoutKind.Sequential)]
        struct OpenFileName
        {
            public int StructSize;
            public IntPtr Owner;
            public IntPtr Instance;
            public IntPtr Filter;
            public IntPtr CustomFilter;
            public int MaxCustomFilter;
            public int FilterIndex;
            public IntPtr File;
            public int MaxFile;
            public IntPtr FileTitle;
            public int MaxFileTitle;
            public IntPtr InitialDirectory;
            public IntPtr Title;
            public int Flags;
            public short FileOffset;
            public short FileExtension;
            public IntPtr DefaultExtension;
            public IntPtr CustomData;
            public IntPtr Hook;
            public IntPtr TemplateName;
            public IntPtr Reserved;
            public int ReservedValue;
            public int FlagsEx;
        }

        [DllImport("Comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetOpenFileNameW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool GetOpenFileName(ref OpenFileName openFileName);
#endif

        public static string Open()
        {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            IntPtr fileBuffer = IntPtr.Zero;
            IntPtr filter = IntPtr.Zero;
            IntPtr title = IntPtr.Zero;
            IntPtr defaultExtension = IntPtr.Zero;

            try
            {
                fileBuffer = Marshal.AllocHGlobal(MaxPathLength * sizeof(char));
                for (int i = 0; i < MaxPathLength; ++i)
                    Marshal.WriteInt16(fileBuffer, i * sizeof(char), 0);

                filter = Marshal.StringToHGlobalUni("CSV 文件 (*.csv)\0*.csv\0所有文件 (*.*)\0*.*\0\0");
                title = Marshal.StringToHGlobalUni("选择足球轨迹 CSV 文件");
                defaultExtension = Marshal.StringToHGlobalUni("csv");

                OpenFileName dialog = new OpenFileName
                {
                    StructSize = Marshal.SizeOf(typeof(OpenFileName)),
                    Filter = filter,
                    FilterIndex = 1,
                    File = fileBuffer,
                    MaxFile = MaxPathLength,
                    Title = title,
                    DefaultExtension = defaultExtension,
                    Flags = OfnExplorer | OfnFileMustExist | OfnPathMustExist | OfnNoChangeDir
                };

                return GetOpenFileName(ref dialog) ? Marshal.PtrToStringUni(fileBuffer) : null;
            }
            finally
            {
                if (fileBuffer != IntPtr.Zero) Marshal.FreeHGlobal(fileBuffer);
                if (filter != IntPtr.Zero) Marshal.FreeHGlobal(filter);
                if (title != IntPtr.Zero) Marshal.FreeHGlobal(title);
                if (defaultExtension != IntPtr.Zero) Marshal.FreeHGlobal(defaultExtension);
            }
#else
            throw new PlatformNotSupportedException("当前文件选择器只支持 Windows 桌面可执行程序。\n");
#endif
        }
    }
}
