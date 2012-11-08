namespace Daemaged.GitInfoPlanter

#if INTERACTIVE
#I "..\deplibs"
#r "NGit.dll"
#r "Sharpen.dll"
#r "Mono.Cecil.dll"
#r "Mono.Cecil.Pdb.dll"
#endif

open System
open Sharpen
open Mono.Cecil
open Mono.Cecil.Pdb
open System
open System.IO
open System.Collections.Generic
open System.Text.RegularExpressions
open System.Reflection;
open System.Threading.Tasks
open Microsoft.FSharp.Text
open System.Reflection
open Mono.Unix.Native;

open NGit
open NGit.Api
open NGit.Storage.File
open NGit.Revwalk
open NGit.Treewalk
open NGit.Treewalk.Filter


#if INTERACTIVE
#load "gitutils.fs"
#endif

// A bunch of git utils
open gitutils

module program =
  let notWindows = Environment.OSVersion.Platform <> PlatformID.Win32NT
  let isWindows = not notWindows
  
  
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
  
  let generateVersionInfoFromGit repoPath originName = 
    let r = repoPath |> buildRepo
    let branchName = r.GetBranch()
       
    let hc = match r |> getHead with
      | None -> raise (Exception("Cant parse head commit, bailing out"))
      | Some(c) -> c
    let omc = 
      match r |> getRev ("refs/remotes/" + originName + "/" + branchName) with
        | None -> hc
        | Some(c) -> c
  
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
    let originRevNo = r |> getRevNo omc
    let aheadOfOriginBy = localRevNo - originRevNo
    let aheadOfOriginByStr = 
      match aheadOfOriginBy with
      | 0 -> ""
      | _ -> "+" + aheadOfOriginBy.ToString()

    let branchOrTagName =
      match r.GetTags() |> Seq.tryFind (fun e -> e.Value.GetObjectId() = hc.Id) with
      | Some v when modifiedNum = 0 && aheadOfOriginBy = 0 
          -> v.Key
      | _ -> branchName

    String.Format("{0}/{1}{2}{3}/{4}", branchOrTagName, localRevNo, aheadOfOriginByStr, modifiedStr, hc.Id.Name)
  
  let readAsm sourceAsm noPdb searchDirs verbose =
    // Read the existing assembly
    let sourceInfo = new FileInfo(sourceAsm)
    let pdbFileName = sourceInfo.FullName.Remove(sourceInfo.FullName.Length - sourceInfo.Extension.Length) + ".pdb"
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
  
  let plantInfoIfNeeded gitInfo baseDate (ad : AssemblyDefinition) verbose =
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
  
    let removeCustomAttrTypes (ad : AssemblyDefinition) (t : Type)  =
      let existingAttrIdx = 
        ad.CustomAttributes 
        |> Seq.mapi (fun i attr -> (i, attr))     
        |> Seq.filter (fun (i, attr) -> isAttrOfType t attr)
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
  
    let insertCustomAttr (ad : AssemblyDefinition) (t : Type) (ctorArg : string) =
      // Inject new attributes into the assembly
      let strType = md.TypeSystem.String
      let corlib = md.TypeSystem.Corlib :?> AssemblyNameReference
      let corlibDef = md.AssemblyResolver.Resolve(new AssemblyNameReference ("mscorlib", corlib.Version, PublicKeyToken = corlib.PublicKeyToken))
      let attrDef = corlibDef.MainModule.GetType t.FullName;
      let attrCtor = attrDef.Methods |> Seq.find (fun m -> m.IsConstructor && m.Parameters.Count = 1 && m.Parameters.[0].ParameterType.FullName = strType.FullName)
      let newAttr = new CustomAttribute (md.Import(attrCtor));
      newAttr.ConstructorArguments.Add(new CustomAttributeArgument(strType, ctorArg));
      ad.CustomAttributes.Add(newAttr)
  
    // Get the current version in the assembly definition and generate a file version out of it
    let fileVersionStr = generateFileVersion ad.Name.Version baseDate
    let fileInfoStr = fileVersionStr + "/" + gitInfo
  
    let mutable hasChanges = false
  
    if (currentFileVersionStr <> fileVersionStr) then
      if (verbose) then
        printfn "Detected change in AssemblyFileVersionAttribute"
      removeCustomAttrTypes ad typeof<AssemblyFileVersionAttribute>
      insertCustomAttr ad typeof<AssemblyFileVersionAttribute> fileVersionStr
      hasChanges <- true
  
  
    if (currentFileInfoStr <> fileInfoStr) then
      if (verbose) then
        printfn "Detected change in AssemblyInformationalVersionAttribute"
      removeCustomAttrTypes ad typeof<AssemblyInformationalVersionAttribute> 
      insertCustomAttr ad typeof<AssemblyInformationalVersionAttribute> fileInfoStr
      hasChanges <- true
   
    if (verbose && not hasChanges) then
      printfn "No changes detected %A/%A" currentFileVersionStr currentFileInfoStr
    hasChanges
  
  let writeAsm destAsm noPdb verbose snkp (ad : AssemblyDefinition) = 
    // Force signing the new assembly if we were passed a SNK file ref
    let nullsnkp = ref (null : StrongNameKeyPair)
    if snkp <> nullsnkp then
      let adName = ad.Name
      adName.HashAlgorithm <- AssemblyHashAlgorithm.SHA1
      adName.PublicKey <- (!snkp).PublicKey    
      ad.Name.Attributes <- ad.Name.Attributes ||| AssemblyAttributes.PublicKey
   
    let wp = new WriterParameters(WriteSymbols = ad.MainModule.HasSymbols, StrongNameKeyPair = !snkp)
    if (wp.WriteSymbols) then wp.SymbolWriterProvider <- new PdbWriterProvider()
  
    if verbose then
      let out = 
        match wp.WriteSymbols with
        | true -> ""
        | false -> "out"
      printfn "Writing %A with%A symbols" destAsm out
    ad.Write(new FileStream(destAsm, FileMode.Create), wp)
  
    if notWindows && Path.GetExtension(destAsm) = ".exe" then    
      printfn "Chmodding %A to executable" destAsm
      Syscall.chmod(destAsm, FilePermissions.S_IRWXU ||| FilePermissions.S_IRGRP ||| FilePermissions.S_IXGRP ||| FilePermissions.S_IROTH ||| FilePermissions.S_IXOTH) |> ignore
  
  // Do the whole thing, read, plant the git-info, write
  let patchAssembly sourceAsm destAsm (gitInfo : string) (baseDate : DateTime) noPdb verbose snkp searchDirs =
    try 
      let ad = readAsm sourceAsm noPdb searchDirs verbose 
      let hasChanges = plantInfoIfNeeded gitInfo baseDate ad verbose
      if hasChanges then
        if verbose then
         printfn "Detected changes, writing %A" destAsm
        writeAsm destAsm noPdb verbose snkp ad
    with
    | _ as ex
      -> printfn "Encountered %A while trying to process %A" ex sourceAsm
  
  
  [<EntryPoint>]  
  let main (args : string[]) =
    let argList = new List<string>()
    let addArg s = argList.Add(s)
    
    let verbose = ref false
    let noPdb = ref false
    let skipMissing = ref false
    let useTasks = ref false
    let printVersion = ref false
    let keyPair = ref (null : StrongNameKeyPair)
    let searchDirs = new List<string>()
    let repoDir = ref ""
    let originName = ref "origin"
    let baseDate = ref DateTime.Now
    let snkp fn = new StrongNameKeyPair(File.Open(fn, FileMode.Open))
    let ic = System.Globalization.CultureInfo.InvariantCulture
    let argsep = match isWindows with
                   | true -> ';'
                   | false -> ':'
                   
    
    let specs =
      ["-v",             ArgType.Set verbose,                                          "Display additional information"
       "--version",      ArgType.Set printVersion,                                     "Display version info"
       "--repo",         ArgType.String (fun s -> repoDir := s),                       "Path the git repository"
       "--origin",       ArgType.String (fun s -> originName := s),                       "Path the git repository"
       "--nopdb",        ArgType.Set noPdb,                                            "Skip creation of PDB files"
       "--skip-missing", ArgType.Set skipMissing,                                      "Skip missing input files silently"
       "--parallel",     ArgType.Set useTasks,                                         "Execute task in parallel on all available CPUs"   
       "--search-path",  ArgType.String 
                            (fun s -> searchDirs.AddRange(s.Split(argsep))),            "Base date for build date"     
       "--basedate",     ArgType.String 
                            (fun s -> baseDate := 
                                 DateTime.ParseExact(s, "yyyy-MM-dd", ic)),            "Base date for build date"     
       "--keyfile",      ArgType.String (fun s -> keyPair := snkp s),                  "Key pair to sign the assembly with"

       "--",             ArgType.Rest addArg,                                        "Stop parsing command line"
  
      ] |> List.map (fun (sh, ty, desc) -> ArgInfo(sh, ty, desc))
   
    let () =
      ArgParser.Parse(specs, addArg)

    if !printVersion then
      let asm = Assembly.GetEntryAssembly()
      let fva = 
        asm.GetCustomAttributes(typeof<AssemblyFileVersionAttribute>, false) |> Seq.head :?> AssemblyFileVersionAttribute
      let fiva = 
        asm.GetCustomAttributes(typeof<AssemblyInformationalVersionAttribute>, false) |> Seq.head :?> AssemblyInformationalVersionAttribute              
      printfn "%s version %s" (asm.GetName().Name) fiva.InformationalVersion
      System.Environment.Exit(0)
    
    if (!verbose) && searchDirs.Count > 0 then 
      printfn "Search path is %A" (String.Join(":", searchDirs))

    let output = argList.[argList.Count - 1]
    
    argList.RemoveAt(argList.Count - 1)  
    let inputList = List.ofSeq argList
    if (!verbose) then
      printfn "Input file list: %A" inputList    
      if !keyPair <> null then
        printfn "key-pair=%A" (!keyPair).PublicKey
  
    let realInputList = match !skipMissing with
      | true -> inputList |> List.map (fun i -> new FileInfo(i)) |> List.filter (fun fi -> fi.Exists)
      | false -> inputList |> List.map (fun i -> new FileInfo(i))
  
    if (not(!skipMissing) && (realInputList |> Seq.exists (fun x -> not(x.Exists)))) then
      printfn "Some input files are missing..., aborting"
      Environment.Exit(-1)
      
    let outputList = 
     match List.length inputList with
       | 1 -> [output]
       | _ -> realInputList |> List.map (fun fi -> Path.Combine(output, fi.Name))
    
    let gitInfo = ref "Unknown"
    try
      gitInfo := generateVersionInfoFromGit !repoDir !originName
    with
      | :? Exception as ex -> 
          printfn "Failed to process git repo %s" !repoDir
          System.Environment.Exit(-1)
       
    if (!verbose) then
      printfn "About to plant \'%A\' as the git version info" gitInfo
  
    if (!verbose) then
      printfn "Output file list: %A" outputList
    
    if !useTasks then
      let tasks = 
        outputList 
        |> Seq.zip realInputList 
        |> Seq.map (fun (i, o) -> Task.Factory.StartNew(new Action(fun () -> patchAssembly i.FullName o !gitInfo !baseDate !noPdb !verbose keyPair searchDirs)))    
        |> Seq.toArray
      Task.WaitAll(tasks)    
    else
      outputList 
      |> Seq.zip realInputList 
      |> Seq.iter (fun (i, o) -> patchAssembly i.FullName o !gitInfo !baseDate !noPdb !verbose keyPair searchDirs)
  
    0 
  