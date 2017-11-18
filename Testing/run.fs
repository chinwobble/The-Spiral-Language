﻿open Spiral.Lib
open Spiral.Tests

let learning =
    "Learning",[option;cuda;extern_;option;console],"The deep learning module.",
    """
open Cuda
open Extern
open Console

inl smartptr_create ptr =
    inl ptr_ty = type (ptr)
    open Option
    inl cell = some ptr |> ref
    function
    | .Dispose -> cell := none ptr_ty
    | .Try -> cell()
    || () ->
        match cell() with
        | [Some: x] -> x
        | _ -> failwith ptr_ty "A Cuda memory cell that has been disposed has been tried to be accessed."
    |> stack // Unless this closure is converted to a layout type, the CUdeviceptr gets manifested as a runtime type and gives a type error.

///// n is the number of args the create function has.
inl safe_alloc n create =
    if lit_is n = false then error_type "n need to be static."
    inl rec loop vars = function
        | 0 ret ->
            inl tns = Tuple.foldr (inl x create -> create x) vars create
            inl r = ret tns
            HostTensor.map_tensor (inl x -> x.ptr.Dispose) tns |> ignore
            r
        | n x -> loop (x :: vars) (n-1)
    function
    | .unsafe -> create
    | x -> loop () n x

inl allocator size =
    inl to_float x = Operators(.float,x,x)
    inl to_int x = FSU.StaticMethod .int64 x int64
    inl to_uint x = FSU.StaticMethod .uint64 x uint64
    inl pool = 
        inl size = 
            match size with
            | _ : float64 -> context.GetDeviceInfo().get_TotalGlobalMemory() |> to_int |> to_float |> (*) size |> to_int
            | _ : int64 -> size
        {size ptr=context.AllocateMemory (SizeT size) |> smartptr_create}

    inl stack = system.System.Collections.Generic."Stack`1"(pool)()

    inl allocate =
        inl smartptr_ty = type (pool.ptr)
        inl f x = x.ptr().Pointer |> to_uint, x.size |> to_uint
        inl pool_ptr, pool_size = f pool
        met rec remove_disposed_and_return_the_first_live ret =
            if stack.get_Count() > 0i32 then 
                inl t = stack.Peek() 
                match t.ptr.Try with
                || [Some: ptr] -> ret (ptr.Pointer |> to_uint, t.size |> to_uint)
                | _ -> stack.Pop() |> ignore; remove_disposed_and_return_the_first_live ret 
            else join (ret (pool_ptr, 0u64))
            : smartptr_ty
        inl (!dyn size) ->
            inb top_ptr, top_size = remove_disposed_and_return_the_first_live
            inl pool_used = top_ptr - pool_ptr + top_size
            assert (to_uint size + pool_used <= pool_size) "Cache size has been exceeded in the allocator."
            inl cell = {size ptr=top_ptr + top_size |> SizeT |> CUdeviceptr |> smartptr_create}
            stack.Push cell
            cell.ptr

    {allocate}

inl CudaTensor allocator =
    open HostTensor
    inl create_ar size1d elem_type = 
        inl ptr = allocator.allocate (size1d * unsafe_convert int64 (sizeof elem_type))
        function // It needs to be like this rather than a module so toa_map does not split it.
        | .elem_type -> elem_type
        | .ptr -> ptr
    inl create {layout elem_type size} = map_tensor (create_ar (total_size size)) {layout size ar=type (elem_type)}

    inl from_host_array ar =
        inl elem_type = ar.elem_type
        inl size = Array.length ar |> unsafe_convert int64
        inl t = create_ar size elem_type
        context(.CopyToDevice,ar,(t.ptr(), ar))
        t

    inl from_host_tensor = map_tensor from_host_array

    inl to_host_array size1d ar =
        inl elem_type = ar.elem_type
        inl ptr = ar.ptr()
        inl t = Array.create elem_type size1d
        context(.CopyToHost,t,(t,ptr))
        context.Synchronize()
        t

    inl to_host_tensor {size} & tns = map_tensor (to_host_array (total_size size)) tns
    inl ptr ar = 
        inl ptr, elem_type = ar.ptr(), ar.elem_type
        !UnsafeCoerceToArrayCudaGlobal(ptr,elem_type)
    inl to_device_tensor_form = map_tensor ptr

    inl zip = function
        | x :: xs as l ->
            inl {size=sa layout=la} = x
            Tuple.iter (inl {size=sb layout=lb} -> 
                assert (sa=sb) "The sizes of all the tensors in zip must be the same in order to be zipped"
                assert (eq_type la lb) "The layouts of all the tensors must have the same format."
                )
            match la with
            | .aot -> error_type "Array of tuples tensor layout is currently not supported."
            | .toa -> {size=sa layout=la ar = Tuple.map (inl {ar} -> ar) l}
        | () -> error_type "Empty input to zip is invalid."
        | x -> x
        
    inl coerce_to_1d {size layout ar} = {layout ar size={from=0; to=total_size size - 1} :: ()}

    // CPS'd variants of the allcoator functions.
    inl create = safe_alloc 1 create
    inl from_host_tensor = safe_alloc 1 from_host_tensor

    {create from_host_tensor to_host_tensor zip elem_type coerce_to_1d to_device_tensor_form total_size} |> stack

open CudaTensor (allocator 0.7)

inl map f (!zip ({size layout} & in)) =
    inl out = create.unsafe {size layout elem_type = type (f (elem_type in))}

    inl in' = coerce_to_1d in |> to_device_tensor_form
    inl out' = coerce_to_1d out |> to_device_tensor_form
    inl near_to = total_size (in'.size)

    run {
        blockDim = 128
        gridDim = 32
        kernel = cuda // Lexical scoping rocks.
            inl from = blockIdx.x * blockDim.x + threadIdx.x
            inl by = gridDim.x * blockDim.x
            Loops.for {from near_to by body=inl {i} ->
                HostTensor.set_unsafe in' i (f (HostTensor.index_unsafe out' i))
                }
        } |> ignore

    out

inl map = safe_alloc 2 map

open Console

inl (>>=) a b ret = a <| inl a -> b a ret
inl succ a ret = ret a

inl map_op_option x =
    open Option
    match dyn (none int64) with
    | [Some: x] -> 99
    | [None] -> x*2

inl map_op x =
    inl add (x, y) = x + y
    inl f = term_cast add (x,x)
    f (x,x)

inl program = 
    inl host_tensor = HostTensor.init 8 id
    inm device_tensor = from_host_tensor host_tensor >>= map map_op
    inl {ar} = to_host_tensor device_tensor
    succ (Array.show_array ar |> writeline)

program id
    """

rewrite_test_cache None //(Some(40,80))

//output_test_to_temp @"C:\Users\Marko\Source\Repos\The Spiral Language\Temporary" learning
//|> printfn "%s"
//|> ignore