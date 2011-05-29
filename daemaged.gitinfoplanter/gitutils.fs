module gitutils

open System
open System.IO
open System.Text.RegularExpressions
open System.Collections.Generic
open Sharpen

open NGit
open NGit.Api
open NGit.Storage.File
open NGit.Revwalk
open NGit.Treewalk
open NGit.Treewalk.Filter

let buildRepo repoPath = 
  (new FileRepositoryBuilder()).SetWorkTree(new FilePath(repoPath)).Build()

let getRev revStr (r : Repository) = 
  let headId = r.Resolve(revStr);
  let rw = new RevWalk(r)
  rw.ParseCommit (headId)  

let getHead (r : Repository) =   
  getRev Constants.HEAD r

let getRepoStatus (r : Repository) = 
  let diff = new IndexDiff(r, Constants.HEAD, new FileTreeIterator(r))  
  ignore(diff.Diff());
  new Status(diff);

let modifiedPaths (stat : Status) = 
  stat.GetModified() |> 
  Seq.append (stat.GetAdded()) |> 
  Seq.append (stat.GetMissing()) |> 
  Seq.append (stat.GetChanged()) |>
  Seq.append (stat.GetRemoved())

let getRevNo (rc : RevCommit) (r : Repository) = 
  let rw = new RevWalk(r)
  rw.SetRetainBody(false)
  rw.MarkStart(rc)
  rw |> Seq.length

let (|Regex|_|) pattern input =
  let m = Regex.Match(input, pattern)
  if m.Success then Some(List.tail [ for g in m.Groups -> g.Value ])
  else None

// Some crude method of filtering out paths that a git submodules
let filterSubmodules (r : Repository) (fileList : seq<string>) =
  let workTree = r.WorkTree.ToString()
  let gitmodules = Path.Combine(workTree, ".gitmodules")
  let lines = File.ReadAllLines(gitmodules)
  let applySubmoduleRE l = 
    match l with 
    | Regex @"^\s*path\s*=\s*(?<submodule_path>.+?)$" [ submodule_path ] -> submodule_path
    | _ -> ""
  let submodulePaths = lines |> Seq.map (fun l -> applySubmoduleRE l) |> Seq.filter (fun s -> not(String.IsNullOrEmpty(s)))
  let smSet = new HashSet<string>(submodulePaths)
  fileList |> Seq.filter (fun p -> not(smSet.Contains(p)))
  