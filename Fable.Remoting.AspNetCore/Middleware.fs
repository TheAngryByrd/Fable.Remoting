namespace Fable.Remoting.AspNetCore

open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http

open System.IO
open System.Threading.Tasks 
open Fable.Remoting.Server
open FSharp.Control.Tasks

type HttpFuncResult = Task<HttpContext option>
type HttpFunc = HttpContext -> HttpFuncResult
type HttpHandler = HttpFunc -> HttpFunc


/// The parts from Giraffe needed to simplify the middleware implementation 
module internal FromGiraffe = 
    let writeStringAsync (input: string) (ctx: HttpContext) = 
        task {
            let bytes = System.Text.Encoding.UTF8.GetBytes(input)
            do! ctx.Response.Body.WriteAsync(bytes, 0, bytes.Length)
            return Some ctx
        }
    let compose (handler1 : HttpHandler) (handler2 : HttpHandler) : HttpHandler =
        fun (final : HttpFunc) ->
            let func = final |> handler2 |> handler1
            fun (ctx : HttpContext) ->
                match ctx.Response.HasStarted with
                | true  -> final ctx
                | false -> func ctx
    let (>=>) = compose
    let setResponseBody (response: obj) : HttpHandler = 
        fun (next : HttpFunc) (ctx : HttpContext) -> 
            task {
                let responseBody = DynamicRecord.serialize response 
                return! writeStringAsync responseBody ctx
            }
    
    /// Sets the content type of the Http response
    let setContentType (contentType: string) : HttpHandler = 
        fun (next : HttpFunc) (ctx : HttpContext) -> 
            task {
                ctx.Response.ContentType <- contentType
                return Some ctx 
            }

    /// Sets the body of the response to type of JSON
    let setBody value : HttpHandler = 
        setResponseBody value 
        >=> setContentType "application/json; charset=utf-8"
    
    /// Used to forward of the Http context
    let halt : HttpHandler = 
      fun (next : HttpFunc) (ctx : HttpContext) ->
        task { return None }

    let fail (ex: exn) (routeInfo: RouteInfo<HttpContext>) (options: RemotingOptions<HttpContext, 't>) : HttpHandler = 
      fun (next : HttpFunc) (ctx : HttpContext) -> 
        task {
            match options.ErrorHandler with 
            | None -> return! setBody (Errors.unhandled routeInfo.methodName) next ctx 
            | Some errorHandler -> 
                match errorHandler ex routeInfo with 
                | Ignore -> return! setBody (Errors.ignored routeInfo.methodName) next ctx  
                | Propagate error -> return! setBody (Errors.propagated error) next ctx  
        }

    let runFunction func impl options args : HttpHandler = 
      fun (next : HttpFunc) (ctx : HttpContext) -> 
        task {
            let! functionResult = Async.StartAsTask (Async.Catch (DynamicRecord.invokeAsync func impl args)) 
            match functionResult with
            | Choice.Choice1Of2 output -> 
                ctx.Response.StatusCode <- 200
                return! setBody output next ctx 
            | Choice.Choice2Of2 ex -> 
               ctx.Response.StatusCode <- 500
               let routeInfo = { methodName = func.FunctionName; path = ctx.Request.Path.ToString(); httpContext = ctx }
               return! fail ex routeInfo options next ctx 
        }

    let buildFromImplementation impl options = 
      let dynamicFunctions = DynamicRecord.createRecordFuncInfo impl
      let typeName = impl.GetType().Name   
      fun (next : HttpFunc) (ctx : HttpContext) -> task {
        let foundFunction = 
          dynamicFunctions 
          |> Map.tryFindKey (fun funcName _ -> ctx.Request.Path.Value = options.RouteBuilder typeName funcName) 
        match foundFunction with 
        | None -> return! halt next ctx   
        | Some funcName -> 
            let func = Map.find funcName dynamicFunctions
            match ctx.Request.Method.ToUpper(), func.Type with  
            | "GET", NoArguments _ ->  
                return! runFunction func impl options [|  |] next ctx  
            | "GET", SingleArgument(input, _) when input = typeof<unit> ->
                return! runFunction func impl options [|  |] next ctx    
            | "POST", SingleArgument(input, _) when input = typeof<unit> -> 
                return! runFunction func impl options [|  |] next ctx  
            | "POST", _ ->      
                let requestBodyStream = ctx.Request.Body
                use streamReader = new StreamReader(requestBodyStream)
                let! inputJson = streamReader.ReadToEndAsync()
                let inputArgs = DynamicRecord.createArgsFromJson func inputJson 
                return! runFunction func impl options inputArgs next ctx
            | _ -> 
                return! halt next ctx
      }

module Remoting = 
  /// Builds the API using a function that takes the incoming Http context and returns a protocol implementation. You can use the Http context to read information about the incoming request and also use the Http context to resolve dependencies using the underlying dependency injection mechanism.
  let fromContext (f: HttpContext -> 't) options = 
    { options with Implementation = FromContext f } 

type RemotingMiddleware<'t>(next          : RequestDelegate,
                            options       : RemotingOptions<HttpContext, 't>) =
    
    do if isNull next then nullArg "next"
    member __.Invoke (ctx : HttpContext) =
      let handler = 
          match options.Implementation with 
          | Empty -> FromGiraffe.halt  
          | StaticValue impl -> FromGiraffe.buildFromImplementation impl options  
          | FromContext createImplementation -> 
              let impl = createImplementation ctx 
              FromGiraffe.buildFromImplementation impl options
      let func : HttpFunc = handler (Some >> Task.FromResult)
      task {
          let! result = func ctx
          if (result.IsNone) then return! next.Invoke ctx
      }

[<AutoOpen>]
module Extensions = 

    type IApplicationBuilder with
      member this.UseRemoting(options:RemotingOptions<HttpContext, 't>) =
          this.UseMiddleware<RemotingMiddleware<'t>> options |> ignore