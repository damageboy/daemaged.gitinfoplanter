#I "..\deplibs"
#r "NGit.dll"
#r "Sharpen.dll"

open NGit
open NGit.Storage.File
open NGit.Revwalk
open System
open Sharpen

Environment.CurrentDirectory
let repoPath = @"c:\\projects\\private\\daemaged.gitversionpatcher\\.git"
//let repoPath = "/projects/private/sharptrader.git/.git"

let r = (new FileRepositoryBuilder()).SetWorkTree(new FilePath(repoPath)).Build();



let getHead (r : Repository) = 
  let headId = r.Resolve(Constants.HEAD);
  let rw = new RevWalk(r)
  rw.ParseCommit (headId)  


let fromC = r.resolve("refs/heads/master");
let toC = r.resolve("refs/remotes/origin/master");


let rw = new RevWalk(r)
let hc = r |> getHead

rw.MarkStart(hc)

rw |> Seq.head

