// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

[<JavaScript>]
module SiteGraphReactor.ExtendedSyntax
open Parser

type 'site strand = 'site list

(* Syntax *)
type 'site hpl =
  { loop : 'site strand
  ; middle : 'site strand
  ; upper_right : 'site strand
  ; lower_right : 'site strand }

type 'site m =
  { upper_left : 'site strand
  ; lower_left : 'site strand
  ; middle : 'site strand
  ; upper_right : 'site strand
  ; lower_right : 'site strand }

type 'site hpr =
  { loop : 'site strand
  ; middle : 'site strand
  ; upper_left : 'site strand
  ; lower_left : 'site strand }

type 'site segment =
  | HairpinLeft of 'site hpl
  | Middle of 'site m
  | HairpinRight of 'site hpr

type 'site gate =
  | Singleton of 'site segment
  | JoinLower of 'site gate * 'site gate
  | JoinUpper of 'site gate * 'site gate

type 'site species =
  | Strand of 'site strand
  | Gate of 'site gate

type 'site complex = int * 'site species list

type 'site t = Syntax.toehold list * 'site complex list

(* Conversion *)
let binding_counter = ref 0
let fresh_number () =
  let r = !binding_counter
  binding_counter := !binding_counter + 1
  r
let fresh_binding = fresh_number >> sprintf "!%d" (* this cannot be generated by user *)

let melt_double s =
  let complement_domain (d: Strands.domain) =
    { Strands.name = d.name
    ; Strands.complemented = d.complemented |> not
    ; Strands.toehold = d.toehold }
  let complement (site: Syntax.site) =
    { Syntax.domain = site.domain |> complement_domain
    ; Syntax.binding = site.binding }
  let freshen_binding (site: Syntax.site) =
    { Syntax.domain = site.domain
    ; Syntax.binding = fresh_binding () |> Some }
  let s = s |> List.map freshen_binding
  s, s |> List.map complement |> List.rev

type view =
  | One of Syntax.strand
  | Two of Syntax.strand * Syntax.strand

let melt_view = function
  | One s -> [s]
  | Two (u,l) -> [u; l]

let view_segment = function
  | HairpinLeft hpl ->
    let upper, lower = melt_double hpl.middle
    let u = [hpl.lower_right |> List.rev; lower |> List.rev; hpl.loop; upper; hpl.upper_right] |> List.concat
    One u
  | Middle m ->
    let upper, lower = melt_double m.middle
    let u = [m.upper_left; upper; m.upper_right] |> List.concat
    let l = [m.lower_right |> List.rev; lower; m.lower_left |> List.rev] |> List.concat
    Two (u, l)
  | HairpinRight hpr ->
    let upper, lower = melt_double hpr.middle
    let u = [hpr.upper_left; upper; hpr.loop |> List.rev; lower; hpr.lower_left |> List.rev] |> List.concat
    One u

let rec view = function
  | Singleton s -> view_segment s, []
  | JoinLower (l, r) ->
    match view l, view r with
    | (One s1, _), (One s2, _) -> failwith "Circular DNA not yet supported"
    | (One s, al), (Two (u, l), ar) -> Two (u, l @ s), al@ar
    | (Two (u, l), al), (One s, ar) -> One (s @ l), u::al@ar
    | (Two (u1, l1), al), (Two (u2, l2), ar) -> Two (u2, l2 @ l1), u1::al@ar
  | JoinUpper (l, r) ->
    match view l, view r with
    | (One s1, _), (One s2, _) -> failwith "Circular DNA not yet supported"
    | (One s, al), (Two (u, l), ar) -> Two (s @ u, l), al@ar
    | (Two (u, l), al), (One s, ar) -> One (u @ s), l::al@ar
    | (Two (u1, l1), al), (Two (u2, l2), ar) -> Two (u1 @ u2, l2), l1::al@ar

let melt g =
  let v, s = view g
  s @ melt_view v

let to_basic_species = function
  | Strand c -> [c]
  | Gate g -> g |> melt

let to_basic_complexes = (fun (i, s) -> (i, List.collect to_basic_species s)) |> List.map
let to_basic ((ts,cs): Syntax.site t) = (ts, to_basic_complexes cs) : Syntax.t

(* Parsing *)
let mk_hpl loop m (u, l) =
  { loop = loop
  ; middle = m
  ; upper_right = u
  ; lower_right = l }
  |> HairpinLeft |> Singleton

let mk_m (ul, ll) m (ur, lr) =
  { upper_left = ul
  ; lower_left = ll
  ; middle = m
  ; upper_right = ur
  ; lower_right = lr }
  |> Middle |> Singleton

let mk_hpr (u, l) m loop =
  { loop = loop
  ; middle = m
  ; upper_left = u
  ; lower_left = l }
  |> HairpinRight |> Singleton

let alternatives l =
  match l |> List.map Parser.pTry |> List.rev with
  | [] -> choice []
  | h::t ->
    h :: (t |> List.map attempt) |> List.rev |> choice

let strand_gate site =
  let sites = sepBy1 site spaces1

  let upper = sites |> Syntax.bracket "<" ">"
  let lower = sites |> Syntax.bracket "{" "}"
  let double = sites |> Syntax.bracket "[" "]"
  let left_hp = sites |> Syntax.bracket "<" "}"
  let right_hp = sites |> Syntax.bracket "{" ">"

  let overhangs =
    alternatives
      [ upper .>>. lower
      ; lower .>>. upper |>> fun (b, a) -> (a, b)
      ; upper |>> fun a -> (a, [])
      ; lower |>> fun b -> ([], b)
      ; skipString "" |>> fun () -> ([], []) ]

  let hpl = pipe3 left_hp double overhangs mk_hpl
  let m = pipe3 overhangs double overhangs mk_m
  let hpr = pipe3 overhangs double right_hp mk_hpr  
  let segment = alternatives [hpl; m; hpr]

  let connect_upper = Syntax.kw "::" |>> fun _ l r -> JoinUpper (l, r)
  let connect_lower = Syntax.kw ":" |>> fun _ l r -> JoinLower (l, r)
  let connect = attempt connect_upper <|> connect_lower
  let gate = chainl1 segment connect |>> Gate

  let strand = upper <|> (lower |>> List.rev) |>> Strand

  strand, gate

let parse_species site =
  let strand, gate = strand_gate site
  attempt gate <|> strand

let parse_syntax site =
  let strand, gate = strand_gate site

  let strands = (attempt gate <|> strand) |> Syntax.sep_by_bars

  let complex = strands |> Syntax.bracket "[" "]" |> Syntax.counted
  let complexes = complex |> Syntax.sep_by_bars

  let species = attempt (strands |>> fun s -> [(1,s)]) <|> complexes

  spaces >>. sepEndBy Syntax.parse_toehold spaces1 .>>. species .>> spaces .>> eof

let parser = Syntax.parse_name |> Syntax.parse_site |> parse_syntax
let parse = parser |> Syntax.run_parser

let parse_basic = parse >> to_basic

let enzyme name =
  let domain = Syntax.parse_domain name
  let domains = sepBy1 domain spaces1 |>> Array.ofList
  let pair = domains .>>. (Syntax.kw "," >>. domains) |> Syntax.bracket "(" ")"
  Syntax.kw "nick" >>. pair

let parser_enzymes =
  let es = sepEndBy (enzyme Syntax.parse_name) (Syntax.kw ";") |> Syntax.bracket "[" "]"
  let enzymes = (Syntax.kw "enzymes" >>. es) |> opt |>> function Some es -> es | None -> []
  let basic = Syntax.parse_name |> Syntax.parse_site |> parse_syntax |>> to_basic
  spaces >>. enzymes .>>. basic
  
let parse_enzymes = parser_enzymes |> Syntax.run_parser
  