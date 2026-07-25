using SIL.LCAtom.Host.LcmUtils;
using SIL.LCModel;

if (args.Length < 2 || args[0] != "open")
{
    Console.Error.WriteLine("Usage: lcatom open <path-to-.fwdata>");
    return 1;
}

var fwDataPath = Path.GetFullPath(args[1]);
if (!File.Exists(fwDataPath))
{
    Console.Error.WriteLine($"File not found: {fwDataPath}");
    return 1;
}

var loader = new FwDataProjectLoader();
using var cache = loader.LoadCache(fwDataPath);

var entryRepo = cache.ServiceLocator.GetInstance<ILexEntryRepository>();
Console.WriteLine($"Project: {cache.ProjectId.Name}");
Console.WriteLine($"Lexical entries: {entryRepo.Count}");

return 0;
