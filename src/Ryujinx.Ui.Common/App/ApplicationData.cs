﻿using LibHac.Common;
using LibHac.Ns;
using LibHac.Fs;
using LibHac.Fs.Fsa;
using LibHac.FsSystem;
using LibHac.Loader;
using LibHac.Tools.Fs;
using LibHac.Tools.FsSystem;
using LibHac.Tools.FsSystem.NcaUtils;
using Ryujinx.Common.Logging;
using Ryujinx.HLE.FileSystem;
using System;
using System.IO;

namespace Ryujinx.Ui.App.Common
{
    public class ApplicationData
    {
        public bool   Favorite      { get; set; }
        public byte[] Icon          { get; set; }
        public string TitleName     { get; set; }
        public string TitleId       { get; set; }
        public string Developer     { get; set; }
        public string Version       { get; set; }
        public string TimePlayed    { get; set; }
        public double TimePlayedNum { get; set; }
        public string LastPlayed    { get; set; }
        public string FileExtension { get; set; }
        public string FileSize      { get; set; }
        public double FileSizeBytes { get; set; }
        public string Path          { get; set; }
        public BlitStruct<ApplicationControlProperty> ControlHolder { get; set; }
        
        public static string GetApplicationBuildId(VirtualFileSystem virtualFileSystem, string titleFilePath)
        {
            using FileStream file = new(titleFilePath, FileMode.Open, FileAccess.Read);

            Nca mainNca = null;
            Nca patchNca = null;

            if (!System.IO.Path.Exists(titleFilePath))
            {
                Logger.Error?.Print(LogClass.Application, $"File does not exists. {titleFilePath}");
                return string.Empty;
            }

            string extension = System.IO.Path.GetExtension(titleFilePath).ToLower();

            if (extension is ".nsp" or ".xci")
            {
                PartitionFileSystem pfs;

                if (extension == ".xci")
                {
                    Xci xci = new(virtualFileSystem.KeySet, file.AsStorage());

                    pfs = xci.OpenPartition(XciPartitionType.Secure);
                }
                else
                {
                    pfs = new PartitionFileSystem(file.AsStorage());
                }

                foreach (DirectoryEntryEx fileEntry in pfs.EnumerateEntries("/", "*.nca"))
                {
                    using var ncaFile = new UniqueRef<IFile>();

                    pfs.OpenFile(ref ncaFile.Ref, fileEntry.FullPath.ToU8Span(), OpenMode.Read).ThrowIfFailure();

                    Nca nca = new(virtualFileSystem.KeySet, ncaFile.Get.AsStorage());

                    if (nca.Header.ContentType != NcaContentType.Program)
                    {
                        continue;
                    }

                    int dataIndex = Nca.GetSectionIndexFromType(NcaSectionType.Data, NcaContentType.Program);

                    if (nca.Header.GetFsHeader(dataIndex).IsPatchSection())
                    {
                        patchNca = nca;
                    }
                    else
                    {
                        mainNca = nca;
                    }
                }
            }
            else if (extension == ".nca")
            {
                mainNca = new Nca(virtualFileSystem.KeySet, file.AsStorage());
            }

            if (mainNca == null)
            {
                Logger.Error?.Print(LogClass.Application, "Extraction failure. The main NCA was not present in the selected file");

                return string.Empty;
            }

            (Nca updatePatchNca, _) = ApplicationLibrary.GetGameUpdateData(virtualFileSystem, mainNca.Header.TitleId.ToString("x16"), 0, out _);

            if (updatePatchNca != null)
            {
                patchNca = updatePatchNca;
            }

            IFileSystem codeFs = null;

            if (patchNca == null)
            {
                if (mainNca.CanOpenSection(NcaSectionType.Code))
                {
                    codeFs = mainNca.OpenFileSystem(NcaSectionType.Code, IntegrityCheckLevel.ErrorOnInvalid);
                }
            }
            else
            {
                if (patchNca.CanOpenSection(NcaSectionType.Code))
                {
                    codeFs = mainNca.OpenFileSystemWithPatch(patchNca, NcaSectionType.Code, IntegrityCheckLevel.ErrorOnInvalid);
                }
            }

            if (codeFs == null)
            {
                Logger.Error?.Print(LogClass.Loader, "No ExeFS found in NCA");

                return string.Empty;
            }

            const string mainExeFs = "main";

            if (!codeFs.FileExists($"/{mainExeFs}"))
            {
                Logger.Error?.Print(LogClass.Loader, "No main binary ExeFS found in ExeFS");

                return string.Empty;
            }

            using var nsoFile = new UniqueRef<IFile>();

            codeFs.OpenFile(ref nsoFile.Ref, $"/{mainExeFs}".ToU8Span(), OpenMode.Read).ThrowIfFailure();

            NsoReader reader = new NsoReader();
            reader.Initialize(nsoFile.Release().AsStorage().AsFile(OpenMode.Read)).ThrowIfFailure();
            
            return BitConverter.ToString(reader.Header.ModuleId.ItemsRo.ToArray()).Replace("-", "").ToUpper()[..16];
        }
    }
}