﻿//  Copyright 2004-2012 Castle Project - http://www.castleproject.org/
//  Hamilton Verissimo de Oliveira and individual contributors as indicated. 
//  See the committers.txt/contributors.txt in the distribution for a 
//  full listing of individual contributors.
// 
//  This is free software; you can redistribute it and/or modify it
//  under the terms of the GNU Lesser General Public License as
//  published by the Free Software Foundation; either version 3 of
//  the License, or (at your option) any later version.
// 
//  You should have received a copy of the GNU Lesser General Public
//  License along with this software; if not, write to the Free
//  Software Foundation, Inc., 51 Franklin St, Fifth Floor, Boston, MA
//  02110-1301 USA, or see the FSF site: http://www.fsf.org.

namespace Castle.MonoRail.Routing

    open System
    open System.Text
    open System.Globalization
    open System.Collections.Generic
    open System.Runtime.Serialization
    open FParsec


    [<Serializable>]
    type RouteParsingException = 
        inherit Exception
        new (msg) = { inherit Exception(msg) }
        new (info:SerializationInfo, context:StreamingContext) = 
            { 
                inherit Exception(info, context)
            }


    module Internal = 

        type Term = 
        | Greedy of char
        | Literal of string
        | NamedParam of char * string
        | Optional of list<Term>

        let recTermList, recTermListRef = createParserForwardedToRef()

        let (<!>) (p: Parser<_,_>) label : Parser<_,_> =
            fun stream ->
#if ROUTINGDEBUG 
                printfn "%A: Entering %s" stream.Position label
                let reply = p stream
                printfn "%A: Leaving %s (%A)" stream.Position label reply.Status
                reply
#else
                p stream
#endif

        let eol : Parser<unit,'u> =
            fun stream ->
                if stream.IsEndOfStream then Reply(())
                else Reply(Error, expectedString "end of line")

        let ident = 
            let isValidIdChar c = isLetter c || isDigit c  
            many1Satisfy2L isLetter isValidIdChar "parameter name"  <!> "ident"
            
        let startOfSegment = 
            choice [ pchar '/'; pchar '.' ]          <!> "startOfSegment"

        let literalTerm = 
            let normalChar = satisfy (fun c -> match c with | '*' | '(' | ')' | '#' | '?' | '%' | '/' | '@' | '!' | '\\' | '.' | ':' -> false | _ -> true)
            let escapedChar = pchar '\\' >>. (anyOf "():.*" )
            many1Chars ( escapedChar <|> normalChar ) <!> "literalTerm"
                |>> fun s ->  ('L', s) 

        let namedTerm = 
            (pchar ':' ) .>>. ident                  <!> "namedTerm"
                |>> fun (_,s) -> ('N', s) 
        
        let greedyTerm = 
            (pstring "**") .>> eol    <!> "greedyTerm"
                |>> fun (_) -> ('G', "")

        let namedTermOrLiteralOrGreedy = 
            startOfSegment .>>. choice [ namedTerm;literalTerm;greedyTerm ] 
                |>> fun (i, (k, c)) -> 
                        match k with 
                        | 'L' -> Literal(i.ToString() + c) 
                        | 'N' -> NamedParam(i, c) 
                        | 'G' -> Greedy (i)
                        | _ -> failwith "wtf?"

        let optionalTerm = 
            between (pchar '(') (pchar ')') recTermList |>> Optional 

        let term = 
              choice [
                            optionalTerm
                            namedTermOrLiteralOrGreedy
                     ] 

        do recTermListRef := many1 term

        let grammar  = recTermList .>> eol

        let ParseRouteDefinition(definition:string) : Term list = 
            // http://tools.ietf.org/html/rfc3986
            // The path is terminated by the first question mark ("?") or number sign ("#") character, or by the end of the URI.
            // not valid chars '?', '#', '@', '%'
            // path segments valid chars: az 0-9 AZ ; , . - _ * + & $ ! [](){} ' '   space?
            // (/:controller(/:action(/:id)))(.:format)
            // /literal
            // /literal/:named
            // /literal/:named
            // /resources(1)
            // /Category\(1\)
            match run grammar definition with 
            | Success(result, _, _)   -> result 
            | Failure(errorMsg, _, _) -> (raise(RouteParsingException(errorMsg)))


        let rec RecursiveGenerate (buffer:StringBuilder) (nodeIndex:int) (nodes:list<Term>) (pending:List<string>) (namedParams:IDictionary<string,string>) (defValues:IDictionary<string,string>) = 
            if (nodeIndex > nodes.Length - 1) then
                true, ""
            else
                let node = nodes.[nodeIndex]

                match node with 
                    | Greedy (lit) ->
                        raise(NotImplementedException("Dont know how to generate Url for greedy segments yet"))

                    | Literal (lit) -> 
                        
                        for s in pending do
                            buffer.Append s |> ignore
                        pending.Clear()

                        buffer.Append (lit) |> ignore
                        RecursiveGenerate buffer (nodeIndex+1) nodes pending namedParams defValues

                    | NamedParam (lit,name) -> 

                        let hasParam, paramVal = namedParams.TryGetValue name
                        let hasDefVal, defVal = defValues.TryGetValue name

                        if not hasParam then 
                            false, (sprintf "Missing required parameter for route generation: '%s'" name)
                        else 
                            let value = lit.ToString() + paramVal

                            namedParams.Remove name |> ignore

                            if hasDefVal && (String.Compare(paramVal, defVal, StringComparison.OrdinalIgnoreCase) = 0) then
                                pending.Add value
                            else
                                for s in pending do
                                    buffer.Append s |> ignore
                                pending.Clear()
                                buffer.Append (value) |> ignore

                            RecursiveGenerate buffer (nodeIndex+1) nodes pending namedParams defValues

                    | Optional (lst) -> 
                        // process children of optional node. since it's optional, we dont care for the result
                        // but we continue from where it last succeeded, so we use the returned index going fwd
                        let r, v = RecursiveGenerate buffer 0 lst pending namedParams defValues
                        RecursiveGenerate buffer (nodeIndex+1) nodes pending namedParams defValues


        let rec rec_fill_default_values index (nodes:list<Term>) (namedParams:Dictionary<string,string>) (defValues:IDictionary<string,string>) = 
            if (index < nodes.Length) then 
                let node = nodes.Item(index)
                match node with
                | NamedParam (lit,name) -> 
                    let r, v = defValues.TryGetValue name
                    if r then namedParams.[name] <- v
                | Optional (lst) ->
                    rec_fill_default_values 0 lst namedParams defValues
                | _ -> ignore()
                
                rec_fill_default_values (index + 1) nodes namedParams defValues


        // path = the raw path that came from the Url (no QS)
        // pathIndex = where to start checking
        // cutPathIndex = 
        // nodeIndex = which node to process
        // nodes = list of all nodes
        // namesParams = to save named parameters and their collected/resolved values
        // defValues   = default values for named parameters, if any
        // hasChildren = if true, end of node list should result in success instead not failure
        // withinOptional = signals that recursion if processing nodes within a optional parent node
        // returns: (succeeded, lastIndexProcessed, cutPathIndex) 
        let rec RecursiveMatch (path:string) (pathIndex:int) (cutPathIndex:int) 
                               (nodeIndex:int) (nodes:list<Term>) 
                               (namedParams:Dictionary<string,string>) (defValues:IDictionary<string,string>) hasChildren withinOptional = 
            // there's content to match                      but not enough nodes? 
            if (path.Length - pathIndex > 0) && (nodeIndex > nodes.Length - 1) then
                if hasChildren || withinOptional 
                then true, pathIndex, cutPathIndex
                else false, pathIndex, cutPathIndex
            elif (nodeIndex > nodes.Length - 1) then
                true, pathIndex, cutPathIndex
            else
                let node = nodes.[nodeIndex]
                
                match node with 
                    | Greedy (lit) ->
                        if pathIndex = path.Length then // no chars to match single literal char
                            if lit = '/' then  // special case to denote / is optional
                                namedParams.["GreedyMatch"] <- "/"
                                true, pathIndex, pathIndex
                            else false, pathIndex, -1 // all other cases is not a match
                        else
                            let startsWithChar = path.[pathIndex] = lit // does it start with the single literal char?
                            if not startsWithChar 
                            then false, pathIndex, -1  // not a match
                            else
                                // yes, so everything else in the path is essentially "the match"
                                let rest = path.Substring(pathIndex)
                                namedParams.["GreedyMatch"] <- rest
                                true, (pathIndex + path.Length), pathIndex + 1 // sum to signal everything else was matched
                        
                    | Literal (lit) -> 

                        let cmp = String.Compare(lit, 0, path, pathIndex, lit.Length, StringComparison.OrdinalIgnoreCase)
                        if (cmp <> 0) 
                        then false, pathIndex, -1
                        else
                            let newindex = pathIndex + lit.Length
                            RecursiveMatch path newindex newindex (nodeIndex + 1) nodes namedParams defValues hasChildren withinOptional

                    | NamedParam (lit,name) -> 

                        let cmp = String.Compare(lit.ToString(), 0, path, pathIndex, 1, StringComparison.OrdinalIgnoreCase)
                        if (cmp <> 0) 
                        then false, pathIndex, -1
                        else 
                            let start = pathIndex + 1 // lit.Length

                            let mutable last = path.IndexOfAny([|'/';'.'|], start)
                            last <- (if last <> -1 then last else path.Length)

                            let value = path.Substring(start, last - start)
                            if (value.Length <> 0) then
                                namedParams.[name] <- value
                                RecursiveMatch path last last (nodeIndex + 1) nodes namedParams defValues hasChildren withinOptional
                            
                            elif withinOptional then
                                let r, v = defValues.TryGetValue name
                                if r then 
                                    namedParams.[name] <- v
                                    RecursiveMatch path last last (nodeIndex + 1) nodes namedParams defValues hasChildren withinOptional
                                else 
                                    // false, pathIndex
                                    RecursiveMatch path last last (nodeIndex + 1) nodes namedParams defValues hasChildren withinOptional

                            else false, pathIndex, -1

                    | Optional (lst) -> 
                        // process children of optional node. since it's optional, we dont care for the result
                        // but we continue from where it last succeeded, so we use the returned index going fwd
                        let res, index, _ = RecursiveMatch path pathIndex cutPathIndex 0 lst namedParams defValues false true

                        if not res then
                            rec_fill_default_values 0 lst namedParams defValues
                            
                        // continue with other nodes
                        RecursiveMatch path index cutPathIndex (nodeIndex + 1) nodes namedParams defValues hasChildren withinOptional


        

