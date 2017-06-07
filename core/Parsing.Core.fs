﻿module private Parsing.Core

open System.Text.RegularExpressions
open Nonempty
open Block
open Extensions


/// A parser that when given lines, may consume some of them. If it does, it
/// returns blocks created from the consumed lines, and lines remaining.
type OptionParser =
    Lines -> Option<Blocks * Option<Lines>>

/// A parser that consumes at least one of the lines given
type PartialParser =
    Lines -> Blocks * Option<Lines>

/// A parser that consumes all lines given and returns the block created
type TotalParser =
    Lines -> Blocks

type SplitFunction =
    Lines -> Lines * Option<Lines>

type OptionSplitFunction =
    Lines -> Option<Lines * Option<Lines>>
    

//-----------------------------------------------------------------------------
// CREATING PARSERS 
//-----------------------------------------------------------------------------


/// Creates an OptionParser, taking a split function and a function to parse the 
/// lines into blocks
let optionParser (splitter: OptionSplitFunction) (parser: TotalParser): OptionParser =
    splitter >> Option.map (Tuple.mapFirst parser)


/// Creates an OptionParser that will ignore the matched lines
let ignoreParser (splitter: OptionSplitFunction): OptionParser =
    splitter >> Option.map (Tuple.mapFirst (Block.ignore >> Nonempty.singleton))


//-----------------------------------------------------------------------------
// COMBINING PARSERS 
//-----------------------------------------------------------------------------


let rec tryMany (parsers: list<OptionParser>) (lines: Lines): Option<Blocks * Option<Lines>> =
    match parsers with
        | [] -> 
            None
        | p :: ps ->
            match p lines with
                | None ->
                    tryMany ps lines
                | result ->
                    result

             
let rec repeatUntilEnd optionParser partialParser lines: Blocks =

    let (blocks, maybeRemainingLines) =
        optionParser lines
            |> Option.defaultWith (fun unit -> partialParser lines)

    match maybeRemainingLines with
        | None ->
            blocks
        | Some(remainingLines) ->
            blocks + (repeatUntilEnd optionParser partialParser remainingLines)


let takeLinesUntil otherParser parser (Nonempty(headLine, tailLines)) =

    let bufferToBlocks =
        Nonempty.rev >> parser

    let rec loopFrom2ndLine buffer lines =
        match Nonempty.fromList lines with
            | None ->
                ( bufferToBlocks buffer, None)

            | Some (Nonempty(head, tail) as neLines) ->
                match otherParser neLines with
                    | None ->
                        loopFrom2ndLine (Nonempty.cons head buffer) tail

                    | Some result ->
                        result |> Tuple.mapFirst (Nonempty.append (bufferToBlocks buffer))

    loopFrom2ndLine (Nonempty.singleton headLine) tailLines


//-----------------------------------------------------------------------------
// WORKING WITH LINES AND BLOCKS 
//-----------------------------------------------------------------------------


/// Takes a split function, and splits Lines into chunks of Lines
let splitIntoChunks splitFn : (Lines -> Nonempty<Lines>) =
    Nonempty.unfold splitFn


/// Creates a SplitFunction that splits before a line matches the given regex
let beforeRegex (regex: Regex) (Nonempty(head, tail) as lines) =
    match Nonempty.span (not << Line.contains regex) lines with
        | Some res ->
            res
        | None ->
            List.span (not << Line.contains regex) tail
                |> Tuple.mapFirst (fun t -> Nonempty(head, t))
                |> Tuple.mapSecond Nonempty.fromList


/// Creates a SplitFunction that splits after a line matches the given regex
let afterRegex regex : SplitFunction =
    Nonempty.splitAfter (Line.contains regex)


/// Creates a SplitFunction that splits on indent differences > 2
let onIndent tabWidth (Nonempty(firstLine, otherLines)): Lines * Option<Lines> =

    let indentSize =
        Line.leadingWhitespace >> Line.tabsToSpaces tabWidth >> String.length

    let firstLineIndentSize =
        indentSize firstLine
    
    otherLines
        |> List.span
            (fun line -> abs (indentSize line - firstLineIndentSize) < 2)
        |> Tuple.mapFirst (fun tail -> Nonempty(firstLine, tail))
        |> Tuple.mapSecond Nonempty.fromList


/// Convert paragraph lines into a Block. The indent of the first line may be
/// different from the rest. If tidyUpIndents is True, indents are removed from all
/// lines.
let firstLineIndentParagraphBlock tidyUpIndents (Nonempty(headLine, tailLines) as lines) =
    let prefixes =
        if tidyUpIndents then 
            Block.prefixes "" ""
        else
            Block.prefixes
                (Line.leadingWhitespace headLine)
                (List.tryHead tailLines 
                    |> Option.defaultValue headLine
                    |> Line.leadingWhitespace
                )
    let trimmedLines =
        lines |> Nonempty.map (fun (l: string) -> l.TrimStart())

    Block.text (wrappable prefixes trimmedLines)


/// Convert paragraph lines into a Block, in a document where paragraphs can be
/// separated by difference in indent. There is only one indent for the whole
/// paragraph, determined from the first line.
let indentSeparatedParagraphBlock 
    (textType: Wrappable -> Block) (lines: Lines) : Block =

    let prefix =
        Line.leadingWhitespace (Nonempty.head lines)

    let trimmedLines =
            (Nonempty.map String.trimStart lines)
    
    textType (Block.wrappable (Block.prefixes prefix prefix) trimmedLines)


/// Creates an OptionSplitFunction that will take all lines between a start and 
/// end marker (inclusive)
let takeLinesBetweenMarkers 
    (startRegex: Regex, endRegex: Regex)
    (Nonempty(headLine, _) as lines)
    : Option<Lines * Option<Lines>> =

    let takeUntilEndMarker (prefix: string) =
        lines
            |> Nonempty.mapHead (String.dropStart prefix.Length)
            |> afterRegex endRegex
            |> Tuple.mapFirst (Nonempty.replaceHead headLine)

    headLine
        |> Line.tryMatch startRegex
        |> Option.map takeUntilEndMarker


//-----------------------------------------------------------------------------
// COMMON PARSERS 
//-----------------------------------------------------------------------------


/// Ignores blank lines
let blankLines: OptionParser =
    ignoreParser (Nonempty.span Line.isBlank)