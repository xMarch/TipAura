using System.Runtime.InteropServices;

// Common open/save dialogs on a short-lived STA thread, so the render loop keeps running.
internal static class FilePicker
{
    [StructLayout(LayoutKind.Sequential)]
    private struct OpenFileName
    {
        public uint Size;
        public nint Owner, Instance, Filter, CustomFilter;
        public uint MaxCustomFilter, FilterIndex;
        public nint File;
        public uint MaxFile;
        public nint FileTitle;
        public uint MaxFileTitle;
        public nint InitialDirectory, Title;
        public uint Flags;
        public ushort FileOffset, FileExtension;
        public nint DefaultExtension, CustomData, Hook, TemplateName, Reserved;
        public uint ReservedSize, FlagsEx;
    }

    [DllImport("user32.dll")]
    private static extern nint GetActiveWindow();
    [DllImport("ole32.dll")]
    private static extern int OleInitialize(nint reserved);
    [DllImport("ole32.dll")]
    private static extern void OleUninitialize();
    [DllImport("comdlg32.dll", EntryPoint = "GetOpenFileNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetOpenFileName(ref OpenFileName settings);
    [DllImport("comdlg32.dll", EntryPoint = "GetSaveFileNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSaveFileName(ref OpenFileName settings);
    [DllImport("comdlg32.dll")]
    private static extern uint CommDlgExtendedError();

    // patterns is a semicolon list such as "*.yaml;*.yml".
    internal static Task<string?> OpenAsync(string fileTypes, string patterns, string? initialDirectory = null) =>
        Run(fileTypes, patterns, initialDirectory, null, save: false);

    internal static Task<string?> SaveAsync(string fileTypes, string patterns, string? initialDirectory, string? fileName) =>
        Run(fileTypes, patterns, initialDirectory, fileName, save: true);

    private static Task<string?> Run(string fileTypes, string patterns, string? initialDirectory, string? fileName, bool save)
    {
        nint owner = GetActiveWindow();
        string allTypes = Localization.T("All files");
        var result = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                int hr = OleInitialize(0);
                Marshal.ThrowExceptionForHR(hr);
                try { result.SetResult(Show(owner, fileTypes, allTypes, patterns, initialDirectory, fileName, save)); }
                finally { OleUninitialize(); }
            }
            catch (Exception ex) { result.SetException(ex); }
        }) { IsBackground = true, Name = "File picker" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return result.Task;
    }

    private static string? Show(nint owner, string fileTypes, string allTypes, string patterns,
        string? initialDirectory, string? fileName, bool save)
    {
        const int capacity = 32768;
        nint buffer = Marshal.AllocHGlobal(capacity * 2);
        nint filter = Marshal.StringToHGlobalUni($"{fileTypes} ({patterns})\0{patterns}\0{allTypes} (*.*)\0*.*\0\0");
        nint directory = initialDirectory is null ? 0 : Marshal.StringToHGlobalUni(initialDirectory);
        string extension = patterns.Split(';')[0].TrimStart('*', '.');
        nint defaultExtension = Marshal.StringToHGlobalUni(extension);
        try
        {
            var name = (fileName ?? "").AsSpan();
            if (name.Length >= capacity) name = name[..(capacity - 1)];
            unsafe
            {
                name.CopyTo(new Span<char>((void*)buffer, capacity));
                ((char*)buffer)[name.Length] = '\0';
            }
            var settings = new OpenFileName
            {
                Size = (uint)Marshal.SizeOf<OpenFileName>(), Owner = owner, Filter = filter,
                File = buffer, MaxFile = capacity, InitialDirectory = directory, DefaultExtension = defaultExtension,
                // Explorer, no cwd change, plus path/file must exist (open) or overwrite prompt (save).
                Flags = 0x00080000 | 0x00000008 | (save ? 0x00000002u | 0x00000800 : 0x00001000 | 0x00000800)
            };
            if (save ? GetSaveFileName(ref settings) : GetOpenFileName(ref settings)) return Marshal.PtrToStringUni(buffer);
            uint error = CommDlgExtendedError();
            if (error != 0) throw new InvalidOperationException($"File dialog failed (0x{error:X4}).");
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            Marshal.FreeHGlobal(filter);
            Marshal.FreeHGlobal(defaultExtension);
            if (directory != 0) Marshal.FreeHGlobal(directory);
        }
    }
}
