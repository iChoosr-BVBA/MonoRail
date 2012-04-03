﻿namespace Castle.MonoRail

    open System
    open System.Collections.Generic
    open System.Data.OData
    open System.Data.Services.Providers
    open System.Linq
    open System.Text
    open System.Web
    open Castle.MonoRail.OData
    open Castle.MonoRail.Extension.OData


    /// Entry point for exposing EntitySets through OData
    [<AbstractClass>]
    type ODataController<'T when 'T :> ODataModel>(model:'T) =  
        
        member x.Model = model
        member internal x.MetadataProvider = model :> IDataServiceMetadataProvider

        member x.Process(GreedyMatch:string, response:HttpResponseBase, request:HttpRequestBase) = 
            
            let qs = request.Url.Query

            let segments = SegmentParser.parse (GreedyMatch, qs, model)
            let requestInfo = SegmentBinder.bind segments model

            let writer = response.Output

            if segments.Length = 1 then 
                match segments.[0] with 
                | UriSegment.Meta m ->
                    match m with 
                    | MetaSegment.Metadata -> 
                        // application/xml;charset=utf-8
                        response.ContentType <- "application/xml;charset=utf-8"
                        MetadataSerializer.serialize(writer, DataServiceMetadataProviderWrapper(x.MetadataProvider), Encoding.UTF8)
                    | _ -> raise(NotImplementedException("Meta not supported yet"))

                | UriSegment.ServiceDirectory ->
                    // output workspace
                    response.ContentType <- "application/xml;charset=utf-8"
                    AtomServiceDocSerializer.serialize (writer, DataServiceMetadataProviderWrapper(x.MetadataProvider), Encoding.UTF8)

                | UriSegment.EntitySet details ->
                    ()

                | _ -> raise(NotImplementedException("Segment not supported"))


            EmptyResult.Instance