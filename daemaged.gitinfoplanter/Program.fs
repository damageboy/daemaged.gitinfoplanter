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
open Microsoft.FSharp.Text
open System.Reflection

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

let generateVersionInfoFromGit (repoPath : string) = 
  let r = repoPath |> buildRepo
  
  let hc = r |> getHead
  let omc = r|> getRev "refs/remotes/origin/master";  

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
  String.Format("{0}/{1}{2}{3}/{4}", r.GetBranch(), localRevNo, aheadOfOriginByStr, modifiedStr, hc.Id.Name)

// Patch a single assembly with AssemblyFileVersionAttribute + AssemblyInformationalVersionAttribute
// containing computed values that describe the build-version more effectively than an incremental #
let patchAssembly targetAsm targetPatchedAsm (repoDir : string) (baseDate : DateTime) nopdb verbose snkp =

  // Read the existing assembly
  let targetInfo = new FileInfo(targetAsm)
  let pdbFileName = targetInfo.FullName.Remove(targetInfo.FullName.Length - targetInfo.Extension.Length) + ".pdb"
  let pdbExists = (not nopdb) && File.Exists(pdbFileName) 
  let ar = new DefaultAssemblyResolver()
  ar.AddSearchDirectory(@"c:\Windows\Microsoft.NET\Framework64\v4.0.30319")
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
    printfn "Reading %A with%A symbols" targetAsm out
  
  // Do it
  let ad = AssemblyDefinition.ReadAssembly(targetAsm, rp)

  // Force signing the new assembly if we were passed a SNK file ref
  let nullsnkp = ref (null : StrongNameKeyPair)
  if snkp <> nullsnkp then
    let adName = ad.Name
    adName.HashAlgorithm <- AssemblyHashAlgorithm.SHA1
    adName.PublicKey <- (!snkp).PublicKey    
    ad.Name.Attributes <- ad.Name.Attributes ||| AssemblyAttributes.PublicKey

  // Get the main module for future ref
  let md = ad.MainModule

  // Check the attribute type in a cecil compatible fashion
  let isAttrOfType (t : Type) (attr : CustomAttribute) =
    t.FullName = attr.Constructor.DeclaringType.FullName

  // Get the current version in the assembly definition and generate a file version out of it
  let fileVersionStr = generateFileVersion ad.Name.Version baseDate
  let fileInfoStr = fileVersionStr + "/" + (generateVersionInfoFromGit repoDir)

  let strType = md.TypeSystem.String
  let corlib = md.TypeSystem.Corlib :?> AssemblyNameReference
  let corlibDef = md.AssemblyResolver.Resolve(new AssemblyNameReference ("mscorlib", corlib.Version, PublicKeyToken = corlib.PublicKeyToken))

  let fileVersionAttrDef = corlibDef.MainModule.GetType ("System.Reflection.AssemblyFileVersionAttribute");
  let fileInfoAttrDef = corlibDef.MainModule.GetType ("System.Reflection.AssemblyInformationalVersionAttribute");
  
  let fileVersionAttrCtor = fileVersionAttrDef.Methods |> Seq.find (fun m -> m.IsConstructor && m.Parameters.Count = 1)
  let fileInfoAttrCtor = fileInfoAttrDef.Methods |> Seq.find (fun m -> m.IsConstructor && m.Parameters.Count = 1)

  let fileVersionAttr = new CustomAttribute (md.Import(fileVersionAttrCtor));
  fileVersionAttr.ConstructorArguments.Add(new CustomAttributeArgument(strType, fileVersionStr));

  let fileInfoAttr = new CustomAttribute (md.Import(fileInfoAttrCtor));
  fileInfoAttr.ConstructorArguments.Add(new CustomAttributeArgument(strType, fileInfoStr));

  ad.CustomAttributes.Add(fileVersionAttr)
  ad.CustomAttributes.Add(fileInfoAttr)

  // Save the new asm
  let wp = new WriterParameters(WriteSymbols = pdbExists, StrongNameKeyPair = !snkp)
  if (wp.WriteSymbols) then wp.SymbolWriterProvider <- new PdbWriterProvider()

  if verbose then
    let out = 
      match wp.WriteSymbols with
      | true -> ""
      | false -> "out"
    printfn "Reading %A with%A symbols" targetAsm out

  ad.Write(new FileStream(targetPatchedAsm, FileMode.Create), wp)


[<EntryPoint>]  
let main (args : string[]) =
  let argList = new List<string>()
  let addArg s = argList.Add(s)
  
  let replace = new List<string>()
  let verbose = ref false
  let matchList = new List<list<Regex>>()
  let noPdb = ref false
  let keyPair = ref (null : StrongNameKeyPair)
  let repoDir = ref ""
  let baseDate = ref DateTime.Now
  let snkp fn = new StrongNameKeyPair(File.Open(fn, FileMode.Open))
  let ic = System.Globalization.CultureInfo.InvariantCulture
  
  let specs =
    ["-v",        ArgType.Set verbose,                                          "Display additional information"
     "--repo",    ArgType.String (fun s -> repoDir := s),                       "Path the git repository"
     "--nopdb",   ArgType.Set noPdb,                                            "Skip creation of PDB files"
     "--basedate", ArgType.String 
                   (fun s -> baseDate := 
                               DateTime.ParseExact(s, "yyyy-MM-dd", ic)),       "Base date for build date"     
     "--keyfile", ArgType.String (fun s -> keyPair := snkp s),                  "Key pair to sign the assembly with"
     "--",        ArgType.Rest   addArg,                                        "Stop parsing command line"
    ] |> List.map (fun (sh, ty, desc) -> ArgInfo(sh, ty, desc))
 
  let () =
    ArgParser.Parse(specs, addArg)

  let output = argList.[argList.Count - 1]
  
  argList.RemoveAt(argList.Count - 1)  
  let inputList = List.ofSeq argList
  if (!verbose) then
    printfn "inputList=%A" inputList
    printfn "output=%A" output
    if !keyPair <> null then
      printfn "key-pair=%A" (!keyPair).PublicKey

  let outputList = 
   match List.length inputList with
     | 1 -> [output]
     | _ -> inputList |> List.map(fun i -> Path.Combine(output, i))
  
  if (!verbose) then
    printfn "outputList=%A" outputList
     
  //outputList  |> Seq.zip input |> Seq.iter (fun (i, o) -> printfn "patchAssembly %A %A %A %A %A" i o matchList !replace !noPdb)
  outputList  |> Seq.zip inputList |> Seq.iter (fun (i, o) -> patchAssembly i o !repoDir !baseDate !noPdb !verbose keyPair)
  0 
