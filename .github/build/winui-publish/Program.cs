try
{
    switch (args)
    {
        case ["native-archive", var directory, var archive, var rid]:
            NativeArchive.Create(directory, archive, rid);
            return 0;
        case ["resource", var kind, var input, var destination]:
            Compression.Resource(kind, input, destination);
            return 0;
        default:
            Console.Error.WriteLine("Usage: WinUI.Publish native-archive <directory> <archive> <rid> | resource <kind> <input> <destination>");
            return 2;
    }
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    return 1;
}
