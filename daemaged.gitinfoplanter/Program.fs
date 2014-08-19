namespace Daemaged.GitInfoPlanter

#if INTERACTIVE
#I "..\deplibs"
#r "NGit.dll"
#r "Sharpen.dll"
#r "Mono.Cecil.dll"
#r "Mono.Cecil.Pdb.dll"
#endif

open Mono.Cecil
open Mono.Cecil.Pdb
open System
open System.IO
open System.Collections.Generic
open System.Threading.Tasks
open System.Runtime.InteropServices
open Microsoft.FSharp.Text
open System.Reflection
open Mono.Unix.Native;

open NGit

#if INTERACTIVE
#load "gitutils.fs"
#endif

// A bunch of git utils
open gitutils
open cmdlinefixer

module program =
  let notWindows = Environment.OSVersion.Platform <> PlatformID.Win32NT
  let isWindows = not notWindows

  type Options() =
    member val Verbose = false with get, set
    member val SkipPdb = false with get, set
    member val SkipMissing = false with get, set
    member val Parallel = false  with get, set
    member val PrintVersion = false with get, set
    member val KeyPair = null:StrongNameKeyPair with get, set
    member val SearchDirs = new List<string>() with get
    member val RepoDir = String.Empty with get, set
    member val OriginName = "origin" with get, set
    member val TargetPlatform = "v4.0" with get, set
    member val BaseDate = DateTime.Now with get, set
    member val BuildId = null:String with get, set

  let options = new Options()

  // Append a build-date number to the version
  let generateFileVersion (ver : Version) (baseDate : DateTime) =
    ver.ToString() + "." + int(DateTime.Now.Subtract(baseDate).TotalDays).ToString()

  // Use ngit to calculate a very detailed/information git version blob
  // The basic format it:
  // <branch-name>/<rev#>/<commit-id>
  // Where:
  //  <branch-name> -> git branch name
  //  <rev#>        -> number of revision in the current branch
  //                   * When there are local modified/added/removed file an 'M' is appended
  //                   * When the current rev# in the local repo is higher than that of the
  //                     remote repo, a +N is appended where N is the number of commits in the
  //                     local repo that are NOT in the remote repo (origin)
  //  <commit-id>   -> the commit id of the commit, can be a sha1 hash or a tag name if that exists
  // Examples:
  // master/548+4/c769ca88c037e9737b6669d3405c954ff4632d9e
  // master/548+4M/c769ca88c037e9737b6669d3405c954ff4632d9e
  // master/548M/c769ca88c037e9737b6669d3405c954ff4632d9e
  // master/548/c769ca88c037e9737b6669d3405c954ff4632d9e
  // master/548/b457/c769ca88c037e9737b6669d3405c954ff4632d9e (when buildid is specified)

  let generateVersionInfoFromGit repoPath originName  =
    let verbose = options.Verbose
    let (r, repoRoot) = buildRepo repoPath verbose
    let branchName = r.GetBranch()

    if verbose then
      printfn "Current branch is %s" branchName

    let hc = match r |> getHead with
      | None -> raise (Exception("Cant parse head commit, bailing out"))
      | Some(c) -> c

    if verbose then
      printfn "HEAD is @ %s" hc.Id.Name

    let omc =
      match r |> getRev ("refs/remotes/" + originName + "/" + branchName) with
        | None -> hc
        | Some(c) -> c

    if verbose then
      printfn "%s is @ %s" (originName + "/" + branchName) omc.Id.Name

    let modifiedCount (r : Repository) =
      r |> getRepoStatus |> modifiedPaths |> filterSubmodules r |> Seq.length

    let modifiedNum = r |> modifiedCount
    let modifiedStr =
      match modifiedNum with
      | 0 -> ""
      | _ -> "M"

    // For debugging purposes
    //let modifiedList = r |> getRepoStatus |> modifiedPaths |> Seq.toArray
    //printfn "%A" modifiedList

    let localRevNo = r |> getRevNo hc
    let originRevNo = if hc = omc then localRevNo else r |> getRevNo omc
    let aheadOfOriginBy = localRevNo - originRevNo
    let aheadOfOriginByStr =
      match hc = omc with
      | true -> ""
      | false -> "+" + aheadOfOriginBy.ToString()

    let branchOrTagName =
      match r.GetTags() |> Seq.tryFind (fun e -> e.Value.GetObjectId() = hc.Id) with
      | Some v when modifiedNum = 0 && aheadOfOriginBy = 0
          -> v.Key
      | _ -> branchName
    (
      String.Format("{0}/{1}{2}{3}/{4}", branchOrTagName, localRevNo, aheadOfOriginByStr, modifiedStr, hc.Id.Name),
      branchOrTagName,
      localRevNo,
      aheadOfOriginBy,
      modifiedNum > 0,
      hc.Id.Name,
      repoRoot
    )

  let readAsm sourceAsm =
    let noPdb = options.SkipPdb
    let searchDirs = options.SearchDirs
    let verbose = options.Verbose

    // Read the existing assembly
    let sourceInfo = new FileInfo(sourceAsm)
    let pdbFileName = Path.ChangeExtension(sourceInfo.FullName, ".pdb")
    let pdbExists = (not noPdb) && File.Exists(pdbFileName)
    let ar = new DefaultAssemblyResolver()
    // Add search directories such as @"C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.0"
    searchDirs |> Seq.iter (fun sd -> ar.AddSearchDirectory(sd))
    let rp = new ReaderParameters(AssemblyResolver = ar,
                                  ReadSymbols = pdbExists,
                                  ReadingMode = ReadingMode.Immediate)

    // Read the symbols if necessary/specified
    if (rp.ReadSymbols) then rp.SymbolReaderProvider <- new PdbReaderProvider()
    if verbose then
      let out =
        match rp.ReadSymbols with
        | true -> ""
        | false -> "out"
      printfn "Reading %A with%A symbols" sourceAsm out

    // Do it
    try
      AssemblyDefinition.ReadAssembly(sourceAsm, rp)
    with
    | _ as ex
      ->  raise <| new Exception(String.Format("Failed to assembly {0}", sourceAsm), ex)

  let plantInfoIfNeeded gitInfo (metadata : Dictionary<string, Object>) (ad : AssemblyDefinition) =
    let baseDate = options.BaseDate
    let verbose = options.Verbose
    // Get the main module for future ref
    let md = ad.MainModule

    // Check the attribute type in a cecil compatible fashion
    let isAttrOfType (t : Type) (attr : CustomAttribute) =
      t.FullName = attr.Constructor.DeclaringType.FullName

    let findCustomAttribute (ad : AssemblyDefinition) (t : Type) =
      let attrs =
        ad.CustomAttributes
        |> Seq.filter (fun attr -> isAttrOfType t attr)

      if (Seq.isEmpty attrs || (Seq.length attrs) > 1) then
        None
      else
        Some(Seq.head attrs)

    let removeCustomAttrTypes (ad : AssemblyDefinition) t  =
      let existingAttrIdx =
        ad.CustomAttributes
        |> Seq.mapi (fun i attr -> (i, attr))
        |> Seq.filter (fun (i, attr) -> t = attr.Constructor.DeclaringType.FullName)
        |> Seq.map (fun (i, attr) -> i) |> Seq.toArray |> Array.rev
      existingAttrIdx |> Seq.iter ad.CustomAttributes.RemoveAt

    let currentFileVersionStr =
      match findCustomAttribute ad typeof<AssemblyFileVersionAttribute> with
      | None -> ""
      | Some(attr) -> (attr.ConstructorArguments.[0].Value :?> string)

    let currentFileInfoStr =
      match findCustomAttribute ad typeof<AssemblyInformationalVersionAttribute> with
      | None -> ""
      | Some(attr) -> (attr.ConstructorArguments.[0].Value :?> string)

    let insertCustomAttr (ad : AssemblyDefinition) t ctorArg anotherArg =
      // Inject new attributes into the assembly
      let verbose = options.Verbose
      let strType = md.TypeSystem.String
      let corlib = md.TypeSystem.Corlib :?> AssemblyNameReference
      let corlibDef = md.AssemblyResolver.Resolve(new AssemblyNameReference ("mscorlib", corlib.Version, PublicKeyToken = corlib.PublicKeyToken))
      let attrDef = corlibDef.MainModule.GetType t
      if attrDef = null then
        raise <| new Exception("Couldn't find attribute definition for " + t)
      if String.IsNullOrEmpty(anotherArg) then
        if verbose then
          printfn "Adding %s(%A)" t ctorArg
        let attrCtor = attrDef.Methods |> Seq.find (fun m -> m.IsConstructor && m.Parameters.Count = 1 && m.Parameters.[0].ParameterType.FullName = strType.FullName)
        let newAttr = new CustomAttribute (md.Import(attrCtor));
        newAttr.ConstructorArguments.Add(new CustomAttributeArgument(strType, ctorArg));
        ad.CustomAttributes.Add(newAttr)
      else
        if verbose then
          printfn "Adding %s(%A,%A)" t ctorArg anotherArg
        let attrCtor = attrDef.Methods |> Seq.find (fun m ->
          m.IsConstructor &&
          m.Parameters.Count = 2 &&
          m.Parameters.[0].ParameterType.FullName = strType.FullName &&
          m.Parameters.[1].ParameterType.FullName = strType.FullName)
        let newAttr = new CustomAttribute (md.Import(attrCtor));
        newAttr.ConstructorArguments.Add(new CustomAttributeArgument(strType, ctorArg));
        newAttr.ConstructorArguments.Add(new CustomAttributeArgument(strType, anotherArg));
        ad.CustomAttributes.Add(newAttr)

    // Get the current version in the assembly definition and generate a file version out of it
    let fileVersionStr = generateFileVersion ad.Name.Version baseDate
    let fileInfoStr =
      if options.BuildId = null
        then fileVersionStr + "/" + gitInfo
        else fileVersionStr + "/" + options.BuildId + "/" + gitInfo

    let mutable hasChanges = false

    if (currentFileVersionStr <> fileVersionStr) then
      if (verbose) then
        printfn "Detected change in AssemblyFileVersionAttribute"
      removeCustomAttrTypes ad typeof<AssemblyFileVersionAttribute>.FullName
      insertCustomAttr ad typeof<AssemblyFileVersionAttribute>.FullName fileVersionStr null
      if (options.TargetPlatform = "v4.5") then
        removeCustomAttrTypes ad "System.Reflection.AssemblyMetadataAttribute"
        metadata |> Seq.iter (fun kvp -> insertCustomAttr ad "System.Reflection.AssemblyMetadataAttribute" kvp.Key (kvp.Value.ToString()))
      hasChanges <- true

    if (currentFileInfoStr <> fileInfoStr) then
      if (verbose) then
        printfn "Detected change in AssemblyInformationalVersionAttribute"
      removeCustomAttrTypes ad typeof<AssemblyInformationalVersionAttribute>.FullName
      insertCustomAttr ad typeof<AssemblyInformationalVersionAttribute>.FullName fileInfoStr null
      hasChanges <- true

    if (verbose && not hasChanges) then
      printfn "No changes detected %A/%A" currentFileVersionStr currentFileInfoStr
    hasChanges

  /// write the assembly from memory to disk
  let writeAsm destAsm (ad : AssemblyDefinition) =
    let snkp = options.KeyPair
    let verbose = options.Verbose
    // Force signing the new assembly if we were passed a SNK file ref
    let nullsnkp = ref (null : StrongNameKeyPair)
    if snkp <> null then
      let adName = ad.Name
      adName.HashAlgorithm <- AssemblyHashAlgorithm.SHA1
      adName.PublicKey <- snkp.PublicKey
      ad.Name.Attributes <- ad.Name.Attributes ||| AssemblyAttributes.PublicKey

    let wp = new WriterParameters(WriteSymbols = ad.MainModule.HasSymbols, StrongNameKeyPair = snkp)
    if (wp.WriteSymbols) then wp.SymbolWriterProvider <- new PdbWriterProvider()

    if verbose then
      let out =
        match wp.WriteSymbols with
        | true -> ""
        | false -> "out"
      printfn "Writing %A with%A symbols" destAsm out
    ad.Write(new FileStream(destAsm, FileMode.Create), wp)

    if notWindows && Path.GetExtension(destAsm) = ".exe" then
      printfn "chmodding %A to executable" destAsm
      Syscall.chmod(destAsm, FilePermissions.S_IRWXU ||| FilePermissions.S_IRGRP ||| FilePermissions.S_IXGRP ||| FilePermissions.S_IROTH ||| FilePermissions.S_IXOTH) |> ignore

  // Do the whole thing, read, plant the git-info, write
  let patchAssembly sourceAsm destAsm (gitInfo : string) (metadata : Dictionary<string, Object>)  =
    try
      let verbose = options.Verbose
      let ad = readAsm sourceAsm
      let hasChanges = plantInfoIfNeeded gitInfo metadata ad
      if hasChanges then
        if verbose then
         printfn "Detected changes, writing %A" destAsm
        writeAsm destAsm ad
    with
      | ex ->
        printfn "Encountered %A while trying to process %A" ex sourceAsm
        File.Delete(destAsm)
        File.Delete(Path.ChangeExtension(destAsm, ".pdb"))
        reraise()

  [<DllImport("kernel32", CharSet = CharSet.Auto)>]
  extern IntPtr GetCommandLine()

  [<EntryPoint>]
  let main (args : string[]) =
    let argList = new List<string>()
    //let joinedArgs = String.Join(" ", args)
    let fixedArgs = match isWindows with
                   | true ->  GetCommandLine() |> Marshal.PtrToStringAuto |> splitCmdLine |> Seq.skip 1 |> Seq.toArray
                   | false -> args
    let addArg s = argList.Add(s)

    let snkp fn = new StrongNameKeyPair(File.Open(fn, FileMode.Open))
    let parseDate s =
      let ic = System.Globalization.CultureInfo.InvariantCulture
      DateTime.ParseExact(s, "yyyy-MM-dd", ic)
    let argsep = match isWindows with
                   | true -> ';'
                   | false -> ':'

    let o = options
    let specs =
      ["--verbose",      ArgType.Unit   (fun x -> o.Verbose <- true),            "Display additional information"
       "--version",      ArgType.Unit   (fun x -> o.PrintVersion <- true),       "Display version info"
       "--repo",         ArgType.String (fun s -> o.RepoDir <- s),               "Path the git repository"
       "--origin",       ArgType.String (fun s -> o.OriginName <- s),            "Name of origin to compare to"
       "--nopdb",        ArgType.Unit   (fun x -> o.SkipPdb <- true),            "Skip creation of PDB files"
       "--platform",     ArgType.String (fun s -> o.TargetPlatform <- s),        "Specify target platform (.NET 4.0 by default)"
       "--skip-missing", ArgType.Unit   (fun x -> o.SkipMissing <- true),        "Skip missing input files silently"
       "--parallel",     ArgType.Unit   (fun x -> o.Parallel <- true),           "Execute task in parallel on all available CPUs"
       "--search-path",  ArgType.String (fun s -> o.SearchDirs.AddRange(s.Split(argsep))),
                                                                                 "Set the search path for assemblies"
       "--basedate",     ArgType.String (fun s -> o.BaseDate <- parseDate s),    "Base date for build date"
       "--buildid",      ArgType.String (fun s -> o.BuildId <- s),               "Specify an additional build-id (e.g. build-server generated id)"
       "--keyfile",      ArgType.String (fun s -> o.KeyPair <- snkp s),          "Key pair to sign the assembly with"
       "--",             ArgType.Rest addArg,                                    "Stop parsing command line"

      ] |> List.map (fun (sh, ty, desc) -> ArgInfo(sh, ty, desc))

    ArgParser.Parse(fixedArgs, specs, addArg)

    let die msg =
      printfn "Error: %s" msg
      System.Environment.Exit(-2)

    let dieusage msg =
      printfn "Usage error: %s" msg
      ArgParser.Usage specs
      System.Environment.Exit(-1)

    if o.PrintVersion then
      let asm = Assembly.GetEntryAssembly()
      let fva =
        asm.GetCustomAttributes(typeof<AssemblyFileVersionAttribute>, false) |> Seq.head :?> AssemblyFileVersionAttribute
      let fiva =
        asm.GetCustomAttributes(typeof<AssemblyInformationalVersionAttribute>, false) |> Seq.head :?> AssemblyInformationalVersionAttribute
      printfn "%s version %s" (asm.GetName().Name) fiva.InformationalVersion
      System.Environment.Exit(0)

    if (o.Verbose) && o.SearchDirs.Count > 0 then
      printfn "Search path is %A" (String.Join(":", o.SearchDirs))

    if argList.Count < 2 then
      dieusage "a single input and single output must be supplied at the minimum"

    let output = argList.[argList.Count - 1]
    let isOutputDir = Directory.Exists output

    argList.RemoveAt(argList.Count - 1)

    if (o.Verbose) then
      printfn "Input file list: %A" argList
      if o.KeyPair <> null then
        printfn "key-pair=%A" (o.KeyPair).PublicKey

    let fileInfoList = match o.SkipMissing with
      | true -> argList |> Seq.map (fun i -> new FileInfo(i)) |> Seq.filter (fun fi -> fi.Exists)
      | false -> argList |> Seq.map (fun i -> new FileInfo(i))

    let realInputList = fileInfoList |> Seq.distinctBy (fun x -> x.FullName) |> List.ofSeq

    if (not(o.SkipMissing) && (realInputList |> Seq.exists (fun x -> not(x.Exists)))) then
      die "Some input files are missing... aborting"

    if List.isEmpty realInputList then
      die "None of the specified input files exist"
    let outputList =
      if isOutputDir then
        match List.length realInputList with
         | 1 -> [ Path.Combine(output, realInputList.[0].Name) ]
         | _ -> realInputList |> List.map (fun fi -> Path.Combine(output, fi.Name))
      else
        match List.length realInputList with
         | 1 -> [output]
         | _ -> raise (Exception("When processing multiple files, the last argument must be a path to a directory"))

    let gitInfo = ref "Unknown"
    let metaData = new Dictionary<string, Object>()
    try
      let (infoStr, branch, localRevNum, aheadOfBy, isModifiedLocally, commitId, repoRoot) =
        generateVersionInfoFromGit o.RepoDir o.OriginName
      metaData.Add("Branch", branch)
      metaData.Add("Revision #", localRevNum)
      metaData.Add("Ahead By", aheadOfBy)
      metaData.Add("Contains Local Modifications", isModifiedLocally)
      metaData.Add("CommitId", commitId)
      metaData.Add("Build Date", DateTime.Now.Date.ToString("yyyy-MM-dd"))
      metaData.Add("Build Day", DateTime.Now.Subtract(o.BaseDate).TotalDays)
      let hostname = Environment.MachineName
      metaData.Add("Hostname", hostname)
      let username = match Environment.UserDomainName with
        | hostname -> Environment.UserName
        | _ -> Environment.UserDomainName + "\\" + Environment.UserName
      metaData.Add("Username", username)
      metaData.Add("Local Dir", repoRoot)
      if not(String.IsNullOrWhiteSpace(o.BuildId)) then
        metaData.Add("Build Id", o.BuildId)

      gitInfo := infoStr

    with
      | :? Exception as ex ->
          printfn "Failed to process git repo %s: %A" o.RepoDir ex
          System.Environment.Exit(-1)

    if (o.Verbose) then
      printfn "About to plant \'%A\' as the git version info" gitInfo

    if (o.Verbose) then
      printfn "Output file list: %A" outputList

    if o.Parallel then
      let tasks =
        outputList
        |> Seq.zip realInputList
        |> Seq.map (fun (i, o) -> Task.Factory.StartNew(new Action(fun () -> patchAssembly i.FullName o !gitInfo metaData)))
        |> Seq.toArray
      Task.WaitAll(tasks)
    else
      outputList
      |> Seq.zip realInputList
      |> Seq.iter (fun (i, o) -> patchAssembly i.FullName o !gitInfo metaData)

    0