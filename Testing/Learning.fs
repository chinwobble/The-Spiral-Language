﻿module Learning

open Spiral.Lib
open Spiral.Tests
open System.IO

let learning =
    "Learning",[option;lazy_;cuda;extern_;option;console;host_tensor],"The deep learning module.",
    """
open Extern
openb Cuda
open Console

// Auxiliaries
inl divup a b = (a-1)/b+1 // Integer division with rounding up. (a+b-1)/b is another variant on this.
inl lazy = Lazy.lazy
inl zero_of = function
    | _: float32 -> 0f32
    | _: float64 -> 0f64
    | _ -> ()

inl one_of = function
    | _ : float32 -> 1f32
    | _ : float64 -> 1f64

inl enum ty x = FS.StaticField ty x ty

met load_mnist (!dyn filename) =
    inl File_ty = fs [text: "System.IO.File"]
    inl FileStream_ty = fs [text: "System.IO.FileStream"]
    inl FileMode = enum (fs [text: "System.IO.FileMode"])
    inl FileAccess = enum (fs [text: "System.IO.FileAccess"])
    inl FileShare = enum (fs [text: "System.IO.FileShare"])
    inl BinaryReader_ty = fs [text: "System.IO.BinaryReader"]
    inl IPAddress_ty = fs [text: "System.Net.IPAddress"]

    inl netword_to_host_order x = FS.StaticMethod IPAddress_ty .NetworkToHostOrder x int32

    use f = FS.StaticMethod File_ty .Open(filename, FileMode.Open, FileAccess.Read, FileShare.Read) FileStream_ty
    use d = FS.Constructor BinaryReader_ty f

    inl read_int32 x = FS.Method d .ReadInt32 x int32 |> netword_to_host_order
    inl read_bytes n = FS.Method d .ReadBytes n (array uint8)

    inl to_int64 = unsafe_convert int64
    inl to_ints64 = Tuple.map to_int64

    inl magic_number = read_int32()
    match magic_number with
    | 2049i32 -> // Labels
        inl n = read_int32()
        inl ar = read_bytes n 
        HostTensor.init (to_int64 n,10) (inl a ->
            inl x = ar a
            inl b -> if unsafe_convert uint8 b = x then 1.0f32 else 0.0f32
            )
    | x -> // Images
        assert (x = 2051i32) "The magic number must be either 2049 or 2051"
        inl n, rows, cols = read_int32(), read_int32(), read_int32()
        inl size = n,rows*cols
        read_bytes (n * rows * cols) 
        |> HostTensor.array_as_tensor
        |> HostTensor.reshape (to_ints64 size)
        |> HostTensor.map (inl x -> unsafe_convert float32 x / 255f32)

inl load_mnist_tensors mnist_path =
    inl mnist_files = {
        test_images = {file = "t10k-images.idx3-ubyte"; expected_size = 10000,28*28}
        test_labels = {file = "t10k-labels.idx1-ubyte"; expected_size = 10000,10}
        train_images = {file = "train-images.idx3-ubyte"; expected_size = 60000,28*28}
        train_labels = {file = "train-labels.idx1-ubyte"; expected_size = 60000,10}
        }
           
    inl path_type = fs [text: "System.IO.Path"]
    inl combine x = FS.StaticMethod path_type .Combine x string
    
    module_map (inl _ {file expected_size} -> 
        load_mnist (combine (mnist_path, file))
        |> HostTensor.assert_size expected_size
        ) mnist_files

inl smartptr_create ptr =
    inl ptr_ty = type (ptr)
    open Option
    inl cell = some ptr |> ref
    function
    | .Dispose -> cell := none ptr_ty
    | .Try -> cell()
    | () -> join (
        match cell() with
        | .Some, x -> x
        | _ -> failwith ptr_ty "A Cuda memory cell that has been disposed has been tried to be accessed."
        )
    |> stack // Unless this closure is converted to a layout type, the CUdeviceptr gets manifested as a runtime type and gives a type error.

///// n is the number of args the create function has.
inl safe_alloc n create =
    if lit_is n = false then error_type "n need to be static."
    inl rec loop vars = function
        | 0 ret ->
            inl tns = Tuple.foldr (inl x create -> create x) vars create
            inl r = ret tns
            tns.update_body (inl {ar} -> ar.ptr.Dispose) |> ignore
            r
        | n x -> loop (x :: vars) (n-1)
    function
    | .unsafe -> create
    | x -> loop () n x

inl SizeT_type = fs [text: "ManagedCuda.BasicTypes.SizeT"]
inl CUdeviceptr_type = fs [text: "ManagedCuda.BasicTypes.CUdeviceptr"]
inl SizeT = FS.Constructor SizeT_type
inl CUdeviceptr = FS.Constructor CUdeviceptr_type

inl to_uint x = FS.UnOp .uint64 x uint64
inl ptr_to_uint (ptr: CUdeviceptr_type) = FS.Field ptr .Pointer SizeT_type |> to_uint
inl uint_to_ptr (x: uint64) = SizeT x |> CUdeviceptr

inl allocator size ret =
    inl to_float x = FS.UnOp .float x float64
    inl to_int x = FS.UnOp .int64 x int64
    
    inl pool = 
        inl size = 
            match size with
            | _ : float64 -> 
                inl CudaDeviceProperties_type = fs [text: "ManagedCuda.CudaDeviceProperties"]
                FS.Method context .GetDeviceInfo() CudaDeviceProperties_type
                |> inl x -> FS.Method x .get_TotalGlobalMemory() SizeT_type
                |> to_int |> to_float |> (*) size |> to_int
            | _ : int64 -> size
        inl q = FS.Method context .AllocateMemory (SizeT size) CUdeviceptr_type
        {size ptr=smartptr_create q}

    inl pool_type = type pool
    inl stack_type = fs [text: "System.Collections.Generic.Stack"; types: pool_type]
    inl stack = FS.Constructor stack_type ()

    inl allocate =
        inl smartptr_ty = type (pool.ptr)
        inl f x = x.ptr() |> ptr_to_uint, x.size |> to_uint
        inl pool_ptr, pool_size = f pool
        met rec remove_disposed_and_return_the_first_live ret =
            if FS.Method stack .get_Count() int32 > 0i32 then 
                inl t = FS.Method stack .Peek() pool_type
                match t.ptr.Try with
                | .Some, ptr -> join (ret (ptr_to_uint ptr, t.size |> to_uint))
                | _ -> FS.Method stack .Pop() pool_type |> ignore; remove_disposed_and_return_the_first_live ret 
            else join (ret (pool_ptr, 0u64))
            : smartptr_ty
        inl (!dyn size) ->
            inb top_ptr, top_size = remove_disposed_and_return_the_first_live
            inl pool_used = top_ptr - pool_ptr + top_size
            assert (to_uint size + pool_used <= pool_size) "Cache size has been exceeded in the allocator."
            inl cell = {size ptr=top_ptr + top_size |> uint_to_ptr |> smartptr_create}
            FS.Method stack .Push cell unit
            cell.ptr

    ret {allocate}
    inl ptr = pool.ptr
    FS.Method context .FreeMemory (ptr()) unit
    ptr.Dispose

use random = 
    inl generator_type = fs [text: "ManagedCuda.CudaRand.GeneratorType"]
    FS.Constructor (fs [text: "ManagedCuda.CudaRand.CudaRandDevice"]) (FS.StaticField generator_type .PseudoDefault generator_type)

use cublas =
    inl cublas_type = fs [text: "ManagedCuda.CudaBlas.CudaBlas"]
    inl pointer_mode_type = fs [text: "ManagedCuda.CudaBlas.PointerMode"]
    inl atomics_mode_type = fs [text: "ManagedCuda.CudaBlas.AtomicsMode"]
    FS.Constructor cublas_type (enum pointer_mode_type .Host, enum atomics_mode_type .Allowed)

inl operation_type = fs [text: "ManagedCuda.CudaBlas.Operation"]
inl to_operation = function
    | .T -> enum operation_type .Transpose
    | .nT -> enum operation_type .NonTranspose

inl isT = function
    | .T -> true
    | _ -> false

inl isnT = function
    | .nT -> true
    | _ -> false

inl rec Learning {d with allocator stream} =
    // Module float default
    inl float = match d with {float} -> float | _ -> unsafe_convert float32

    open HostTensor
    inl CudaTensor =
        inl cuda_array_create elem_type len = 
            inl ptr = allocator.allocate (len * unsafe_convert int64 (sizeof elem_type))
            function // It needs to be like this rather than a module so toa_map does not split it.
            | .elem_type -> elem_type
            | .ptr -> ptr
        inl create data = create {data with array_create = cuda_array_create}
        inl create_like tns = create {elem_type=tns.elem_type; dim=tns.dim}

        inl from_host_array ar =
            inl elem_type = ar.elem_type
            inl size = array_length ar |> unsafe_convert int64
            inl t = cuda_array_create elem_type size
            FS.Method context .CopyToDevice(t.ptr(), ar) unit
            t

        inl to_host_array size1d ar =
            inl elem_type = ar.elem_type
            inl ptr = ar.ptr()
            inl t = array_create elem_type size1d
            FS.Method context .CopyToHost (t,ptr) unit
            FS.Method context .Synchronize() unit
            t

        inl transfer_template f tns = 
            assert_contiguous tns
            tns.update_body <| inl {body with position ar} ->
                // I do not feel like messing with GC handles in Spiral right now.
                // Allowing a copy with an offset would be easy though. See ManagedCuda's CopyToHost and CopyToDevice.
                assert (position = 0) "Only unviewed arrays are allowed for now."
                {body with ar = f ar}

        inl from_host_tensor = transfer_template from_host_array
        inl to_host_tensor tns = transfer_template (to_host_array (length tns)) tns

        inl DeviceTensorPrimitives = // The DeviceTensor uses the position as the array.
            inl (+) a (b: int64) = !MacroCuda(a,[arg: a; text: " + "; arg: b])
            inl view {data with size ar offsets} = function
                | i :: l -> {data with 
                    ar=ar + i * size
                    offsets=view_offsets (offsets,l)
                    }
                | i -> {data with ar=ar + i * size}
        
            inl merge_offset {data with size ar} {size=size' position=position'} = {data with size=size'; ar=ar + position'}
            {
            view
            get = inl {data with ar} -> ar 0
            set = inl {data with ar} v -> ar 0 <- v
            apply = primitive_apply_template {view merge_offset} 
            }

        inl to_dev_tensor tns = 
            tns.update_body (inl {body with ar position} ->
                inl ptr, elem_type = ar.ptr(), ar.elem_type
                met ar = 
                    inl ptr = ptr_to_uint ptr + unsafe_convert uint64 position |> uint_to_ptr    
                    !UnsafeCoerceToArrayCudaGlobal(ptr,elem_type)

                {body with ar without position}
                ) .replace_module (TensorTemplate DeviceTensorPrimitives)

        inl clear (!to_dev_tensor tns) = 
            assert_contiguous tns
            inl size = length tns
            inl stream = Stream.extract stream
            tns.update_body <| inl {body with ar} ->
                FS.Method context .ClearMemoryAsync (ar,0u8,size * sizeof (ar.elem_type) |> SizeT,stream) unit
            |> ignore

        inl zero_like = create_like >> inl x -> clear x; x

        inl from_host_tensors x ret = 
            inl tensors = toa_map from_host_tensor x
            inl r = ret tensors
            toa_map (inl x -> x.update_body (inl {ar} -> ar.ptr.Dispose)) |> ignore
            r

        // CPS'd variants of the allocator functions.
        inl create = safe_alloc 1 create
        inl from_host_tensor = safe_alloc 1 from_host_tensor
        inl zero_like = safe_alloc 1 zero_like

        {create from_host_tensor from_host_tensors to_host_tensor to_dev_tensor clear zero_like}

    open CudaTensor
    inl CudaKernels =
        inl set_stream_random x = FS.Method x .SetStream (Stream.extract stream) unit
        inl set_stream_cublas x = FS.Method x .set_Stream (Stream.extract stream) unit

        inl random_fill_array op size1d ar =
            inl elem_type = ar.elem_type
            inl gen, dot = "Generate", "."
            match op with
            | .Uniform & .(distribution) ->
                inl args = ar, SizeT size1d
                inl bits = 
                    match elem_type with
                    | _ : float32 -> "32" | _ : float64 -> "64"
                    | _ -> error_type ("Only 32/64 bit float types are supported. Try UInt if you need uint random numbers. Got: ", elem_type)
                !MacroFs(unit,[arg: random; text: dot; text: gen; text: distribution; text: bits; args: args])
            | {op=(.Normal | .LogNormal) & .(distribution) stddev mean} ->
                match stddev with | _: float32 -> () | _ -> error_type "Standard deviation needs to be in float32."
                match mean with | _: float32 -> () | _ -> error_type "Mean needs to be in float32."

                inl args = ar, SizeT size1d, mean, stddev
                inl bits = 
                    match elem_type with
                    | _ : float32 -> "32" | _ : float64 -> "64"
                    | _ -> error_type ("Only 32/64 bit float types are supported. Try UInt if you need uint random numbers. Got: ", elem_type)
                !MacroFs(unit,[arg: random; text: dot; text: gen; text: distribution; text: bits; args: args])
            | .UInt ->
                inl args = ar, SizeT size1d
                inl bits =
                    match elem_type with
                    | _ : uint32 -> "32" | _ : uint64 -> "64"
                    | _ -> error_type "Only 32/64 bit uint types are supported."
                !MacroFs(unit,[arg: random; text: dot; text: gen; text: bits; args: args])

        inl random_fill op (!zip in) =
            set_stream_random random
            inl in' = to_1d in |> to_dev_tensor
            inl len = length in'
            in'.update_body (inl {ar} -> random_fill_array op len ar) |> ignore

        inl random_create_tensor op dsc ret =
            inb device_tensor = create dsc
            random_fill op device_tensor
            ret device_tensor

        inl map_template mode f (!zip in) (!zip out) =
            assert_zip (in, out) |> ignore
            inl in = to_1d in |> to_dev_tensor
            inl out = to_1d out |> to_dev_tensor
            inl near_to = length in

            run {
                stream
                blockDim = 128
                gridDim = 64
                kernel = cuda // Lexical scoping rocks.
                    inl from = blockIdx.x * blockDim.x + threadIdx.x
                    inl by = gridDim.x * blockDim.x
                    Loops.for {from near_to by body=inl {i} -> 
                        inl out, in = out i, in i
                        match mode with
                        | .Add -> out .set (toa_map2 (inl a b -> a + b) (out.get) (f (in.get)))
                        | .Set -> out .set (f (in .get)) 
                        }
                } |> ignore

        inl map f (!zip in) ret =
            inb out = create {dim=in.dim; elem_type = type (f (in.elem_type))}
            map_template .Set f in out
            ret out
            
        /// Flattens the tensor to 1d, maps it, reduces it, and maps it again.
        /// Lazily returns the output. Requires the redo and the neutral element.
        /// Map is optional.
        inl map_redo {d with redo neutral_elem} (!zip (!to_1d in)) ret =
            inl in' = to_dev_tensor in
            inl near_to = length in'
            inl map = match d with {map} -> map | _ -> id

            inl blockDim = 128
            inl gridDim = min 64 (divup near_to blockDim)
            inl elem_type = type (in.elem_type |> map)
            inl ty = elem_type

            inl cub_block_reduce thread_result redo =
                !MacroCuda(ty,[
                    text: "cub::BlockReduce"
                    iter: "<",",",">",[type: ty; arg: blockDim]
                    args: ()
                    text: ".Reduce"
                    args: thread_result, redo])

            inb out = create {elem_type dim=gridDim}
            inl out' = to_dev_tensor out

            run {
                stream
                blockDim
                gridDim
                kernel = cuda 
                    inl from = blockIdx.x * blockDim.x + threadIdx.x
                    inl by = gridDim.x * blockDim.x
                    inl load i = map (in' i .get)
                    inl thread_result = Loops.for {from near_to by state=dyn neutral_elem; body=inl {state i} -> redo state (load i)}

                    inl redo = closure_of (inl a,b -> redo a b) ((ty,ty) => ty)
                    inl block_result = cub_block_reduce thread_result redo
                    if threadIdx.x = 0 then out' (blockIdx.x) .set block_result
                } |> ignore

            inl _ ->
                inl tns = to_host_tensor out
                inl load i = tns i .get
                Loops.for {from=0; near_to=length tns; state=dyn neutral_elem; body=inl {state i} -> redo state (load i)}
            |> lazy // The lazy return here is because transfering to host would block the execution.
            |> ret

        inl len {from near_to} = near_to - from
        inl rows x = x.dim |> inl a,b -> len a
        inl cols x = x.dim |> inl a,b -> len b

        /// General matrix-matrix multiply from cuBLAS. Inplace version
        inl gemm' transa transb alpha A B beta C =
            set_stream_cublas cublas
            inl handle = FS.Method cublas .get_CublasHandle() (fs [text: "ManagedCuda.CudaBlas.CudaBlasHandle"])
            inl native_type = fs [text: "ManagedCuda.CudaBlas.CudaBlasNativeMethods"]
            inl assert_ok status = !MacroFs(unit,[text: "if "; arg: status; text: " <> ManagedCuda.CudaBlas.CublasStatus.Success then raise <| new ManagedCuda.CudaBlas.CudaBlasException"; args: status])
            inl to_dev_tensors = Tuple.map (inl x -> assert_contiguous x; to_dev_tensor x)
            inl call m x = 
                inl x = Tuple.map (function x : int64 -> unsafe_convert int32 x | x -> x) x
                inl status_type = fs [text: "ManagedCuda.CudaBlas.CublasStatus"]
                FS.StaticMethod native_type m x status_type |> assert_ok
            inl conv o x = 
                match o.elem_type with
                | t: float64 | t: float32 -> if eq_type t x then x else unsafe_convert t x
                | _ -> error_type "Only f32/f64 types supported for conversion in gemm."
        
            // -------

            // These two are meant to be called from inside gemm as they lack boundary checks.
            // I've added them to enhance gemm's vector handling capabilities for online learning
            // tasks.

            /// o <- alpha * op(A) * x + beta * o
            /// Matrix-vector multiplication. Inplace version.
            inl gemv transa alpha A x beta o =
                inl m,n = rows A, cols A
                inl lda = m
                inl A,x,o = to_dev_tensors (A,x,o)
                toa_iter3 (inl {ar=A} {ar=x} {ar=o} ->
                    inl alpha, beta = conv o alpha, conv o beta
                    call.cublasSgemv_v2(handle, to_operation transa, m, n, ref alpha, A, lda, x, 1, ref beta, o, 1)
                    ) (A.bodies) (x.bodies) (o.bodies)

            // A <- alpha * x * yT + beta * A (outer product)
            inl ger alpha x y beta a =
                inl max (a,b) = max a b
                inl m = max (rows x, cols x)
                inl n = max (rows y, cols y)

                inl alpha, beta = conv a alpha, conv a beta
                match beta with
                | 0.0f64 | 0.0f32 -> ()
                | _ -> map_template .Set (toa_map ((*) beta)) a a

                inl x,y,a = to_dev_tensors (x,y,a)
                toa_iter3 (inl {ar=x} {ar=y} {ar=a} ->
                    call.cublasSger_v2(handle, m, n, ref alpha, x, 1, y, 1, a, m)
                    ) (x.bodies) (y.bodies) (a.bodies)

            // -------

            inl is_vector x = rows x = 1 || cols x = 1

            inl a_col = if isnT transa then cols A else rows A
            inl b_row = if isnT transb then rows B else cols B
            assert (a_col = b_row) "Colums of a does not match rows of b in GEMM."

            inl m = if isnT transa then rows A else cols A
            inl n = if isnT transb then cols B else rows B
            inl k = a_col
            inl lda = if isnT transa then m else k
            inl ldb = if isnT transb then k else n
            inl ldc = m
        
            assert (m = rows C && n = cols C) "Output matrix dimensions do not match in GEMM."

            // If is outer product call ger
            if a_col = 1 && b_row = 1 then ger alpha A B beta C
            // If the vector is on the right side or both are vectors call gemv normally.
            elif is_vector B then gemv transa alpha A B beta C
            // If the vector is on the left side call gemv with the arguments switched and transposed
            // It does not actually transpose them, just their views. The function should work regardless.
            elif is_vector A then
                inl optb = if isnT transb then .T else .nT
                gemv optb alpha B A beta C
            // Just do the standard matrix multiply
            else
                inl A,B,C = to_dev_tensors (A,B,C)
                toa_iter3 (inl {ar=A} {ar=B} {ar=C} ->
                    inl alpha, beta = conv C alpha, conv C beta
                    call.cublasSgemm_v2(handle, to_operation transa, to_operation transb, m, n, k, ref alpha, A, lda, B, ldb, ref beta, C, ldc)
                    ) (A.bodies) (B.bodies) (C.bodies)

        inl gemm transa transb alpha A B ret =
            inl A_ty, B_ty = A.elem_type, B.elem_type
            assert (eq_type A_ty B_ty) ("Elements of A should be of equal type to B's.", A_ty, " <> ", B_ty)

            inl m = if isnT transa then rows A else cols A
            inl n = if isnT transb then cols B else rows B

            inb C = create {dim=m,n; elem_type = A_ty}
            gemm' transa transb alpha A B 0.0 C
            ret C

        {map map_template map_redo gemm' gemm random_fill random_create_tensor}

    inl AutoDiffPrimitives =
        open CudaKernels
        inl (>>=) a b ret = a <| inl a -> b a ret

        inl fmap on_module on_var =
            inl rec loop = function
                | x :: xs -> loop x :: loop xs
                | {} as x -> on_module x (module_map (const loop))
                | x -> on_var x
            loop

        inl primal_template f = 
            fmap (function
                | {primal adjoint} _ -> f (primal, adjoint)
                | x on_fail -> on_fail x)

        inl primal = primal_template fst id
        inl adjoint = primal_template snd (const ())

        // These two are not intended to be able to make duals of duals.
        // Higher order AD is not supported.
        inl dual_make primal ret = 
            inb adjoint = zero_like primal
            ret {primal adjoint}

        inl dual_make_lazyhost = toa_map <| inl primal -> {primal adjoint=(zero_of >> ref) (primal.elem_type)}
        inl dual_make_host = toa_map <| inl primal -> {primal adjoint=(zero_of >> ref) (type (primal))}
            
        inl is_unit = function
            | () -> false
            | _ -> true

        inl rec filter_units = function
            | x :: x' -> 
                match filter_units x with
                | () -> filter_units x'
                | x -> x :: filter_units x'
            | {} & x ->
                module_filter (inl k (!filter_units (!is_unit x)) -> x) x
                |> inl x -> if eq_type x {} then () else x
            | x -> x

        // What this does is selectively filter out the results of applying f 
        // where the adjoints are missing (in other words constants.)
        inl filter_based_on_adjoints x adjoint =
            inl rec mark_units = function
                | x :: x', y :: y' -> mark_units (x,y) :: mark_units (x',y')
                | {} & x, {} & y -> module_map (inl k x -> mark_units (x,y k)) x
                | _, () -> ()
                | x, _ -> x
            mark_units (x,adjoint) |> filter_units

        inl filter_unit_and_branch x ret =
            match filter_units x with
            | () -> ()
            | x -> ret x

        inl map {fwd bck} in ret =
            inl primal, adjoint = primal in, adjoint in
            inb out = map fwd primal >>= dual_make
            ret (out, inl _ ->
                inl out = match out with {primal adjoint} -> zip (primal, adjoint) .update_body2 (inl P A -> {P A})
                inl bck =
                    inl adjoint = type (adjoint)
                    inl x -> filter_based_on_adjoints (bck x) adjoint
                inb adjoint = filter_unit_and_branch adjoint 
                map_template .Add bck {in=primal; out} adjoint
                )

        inl hostlazy_map {fwd bck} in ret =
            inl primal, adjoint = primal in, adjoint in
            inl out = lazy (inl _ -> toa_map (inl x -> x.value) primal |> fwd) |> dual_make_lazyhost
            ret (out, inl _ ->
                inl out = toa_map2 (inl P A -> {P A = A ()}) (out.primal.value) (out.adjoint)
                inl bck = filter_based_on_adjoints (bck {in=toa_map (inl x -> x.value) primal; out}) adjoint
                inb adjoint = filter_unit_and_branch adjoint 
                toa_map2 (inl a b -> a := a() + b) adjoint bck
                )

        inl force in ret = 
            inl primal, adjoint = toa_map (inl x -> x.value) (primal in), adjoint in
            ret ({primal adjoint}, const ())

        inl host_map {fwd bck} in ret =
            inl primal, adjoint = primal in, adjoint in
            inl out = fwd primal |> dual_make_host
            ret (out, inl _ ->
                inl out = toa_map2 (inl P A -> {P A = A ()}) (out.primal) (out.adjoint)
                inl bck = filter_based_on_adjoints (bck {in=toa_map (inl x -> x.value) primal; out}) adjoint
                inb adjoint = filter_unit_and_branch adjoint 
                toa_map2 (inl a b -> a := a() + b) adjoint bck
                )

        inl map_redo {fwd bck} in ret =
            inl primal, adjoint = primal in, adjoint in
            inb !dual_make_lazyhost out = map_redo fwd primal
            ret (out, inl _ ->
                inl out = toa_map2 (inl P A -> {P A = A ()}) (out.primal.value) (out.adjoint)
                inl bck =
                    inl adjoint = type (adjoint)
                    inl in -> filter_based_on_adjoints (bck {in out}) adjoint
                inb adjoint = filter_unit_and_branch adjoint 
                map_template .Add bck primal adjoint
                )

        inl gemm' alpha A B ret =
            inb C = gemm .nT .nT alpha (primal A) (primal B) >>= dual_make
            ret (C, inl _ ->
                inl on_adjoint B ret =
                    match adjoint B with
                    | () -> ()
                    | B -> ret B
                on_adjoint A (gemm' .nT .T alpha (adjoint C) (primal B) 1.0)
                on_adjoint B (gemm' .T .nT alpha (primal A) (adjoint C) 1.0)
                )

        inl gemm = gemm' 1.0

        inl scalar_mult a b = 
            host_map {
                fwd = inl a,b -> a * b
                bck = inl {in=a,b out={A}} -> A * b, A * a
                } (a,b)
                
        inl scalar_div a b = 
            host_map {
                fwd = inl a,b -> a / b
                bck = inl {in=a,b out={A}} -> A / b, A * -(a / b * b)
                } (a,b)

        inl to y_ty x = 
            host_map {
                fwd = unsafe_convert y_ty 
                bck = inl {in out={A}} -> unsafe_convert in A
                } x

        {map hostlazy_map host_map map_redo gemm' gemm dual_make dual_make_lazyhost scalar_mult scalar_div to force}

    inl AutoDiffOps = 
        open AutoDiffPrimitives

        inl (>>=) a b ret =
            inb a,a_bck = a
            inb b,b_bck = b a
            ret (b, inl _ -> b_bck(); a_bck())

        inl succ x ret = ret (x, const ())

        inl multiply_by_adjoint f {d with out={A P} in} = toa_map ((*) A) (f {in out=P})
        inl activation d = map {d with bck = multiply_by_adjoint self }
        inl sigmoid = activation {
            fwd = inl x -> float 1 / (float 1 + exp -x)
            bck = inl {out} -> out * (float 1 - out)
            }

        inl error {fwd bck} (input,_ as x) = 
            inl batch_size = input .primal .dim |> fst |> HostTensor.span
            inl div_by_minibatch_size x = x / float batch_size
            map_redo {
                fwd = {
                    map = fwd
                    redo = (+)
                    neutral_elem = float 0
                    }
                bck = multiply_by_adjoint bck
                } x
            >>= hostlazy_map {fwd = div_by_minibatch_size; bck = inl {out={A}} -> div_by_minibatch_size A}

        inl Error = {
            square = error {
                fwd = inl (x,y) -> (y - x) * (y - x)
                bck = inl {in=x,y} -> float -2 * (y - x), float 2 * (y - x)
                }
            cross_entropy = error {
                fwd = inl x, y -> -(y * log x + (float 1 - y) * log (float 1 - x))
                bck = inl {in=x, y} -> x * (x-y) / (float 1 - x), log (float 1 - x) - log x
                }
            }

        {sigmoid Error gemm gemm' (>>=) succ force dual_make}

    inl Optimizers = 
        open CudaKernels
        inl sgd learning_rate {primal adjoint} = 
            map_template .Set (inl A,P -> toa_map2 (inl A P -> P - learning_rate * A) A P) (adjoint,primal) primal
            CudaTensor.clear adjoint 

        {sgd}

    inl FeedforwardLayers = 
        open AutoDiffOps

        inl layer initializer activation hidden_size next_layer input_size ret =
            inb weight = initializer (input_size, hidden_size)
            inb weight = dual_make weight
            inb {update_weights apply} = next_layer hidden_size
            ret {
                update_weights = inl f -> f weight; update_weights f
                apply = inl input -> gemm input weight >>= activation >>= apply
                }

        inl sigmoid_initializer dim = 
            inl sqrt x = FS.UnOp .sqrt x x
            inl stddev = sqrt (2f32 / unsafe_convert float32 (Tuple.foldl (+) 0 dim))
            CudaKernels.random_create_tensor {op=.Normal; stddev mean=0f32} {dim elem_type=type (float 0)}

        inl sigmoid = layer sigmoid_initializer sigmoid
        inl init = 
            inl fin _ ret =
                ret {
                    update_weights = const ()
                    apply = succ
                    }            
            function
            | _ :: _ as layers -> Tuple.foldr (<|) layers fin
            | layer -> layer fin
        inl with_error error network = {network with apply = inl (input,label) -> self input >>= inl input -> error (input,label)}
        
        inl run {d with network={update_weights apply} input label} =
            inl dim1 x = x.dim |> fst
            inl to = unsafe_convert
            inl span_size = HostTensor.span // near_to - from

            inl {span with from near_to} = dim1 input
            assert (dim1 input = dim1 label) "Training and test set need to have to equal first dimensions."

            inl minibatch_size = match d with {minibatch_size} -> minibatch_size | _ -> span_size span

            Loops.for {from near_to; state=0.0; by=minibatch_size; body = inl {state i=from} ->
                inl near_to = min (from + minibatch_size) near_to
                inl span = {from near_to}
                inl view x = x.view_span span
                inb {primal adjoint}, bck = apply (view input, view label)
                inl primal = primal.value
                string_format "On minibatch {0}. Error = {1}" (show span, primal) |> writeline

                match d with
                | {optimizer} ->
                    writeline "Running the backwards phase..."
                    adjoint := one_of primal
                    bck() // Runs the backwards pass.
                    update_weights optimizer
                | _ -> ()

                inl unscaled_cost = to float64 primal * to float64 (span_size span)
                state + unscaled_cost
                }
            |> inl unscaled_cost -> 
                writeline "-----"
                writeline "Batch done."
                string_format "Average of batch costs is {0}." (unscaled_cost / to float64 (span_size span)) |> writeline
                writeline "-----"
            
        {run with_error init sigmoid layer}
    {CudaTensor CudaKernels AutoDiffPrimitives AutoDiffOps Optimizers FeedforwardLayers}

inb allocator = allocator 0.7
use stream = Stream.create()
open Learning {allocator stream}
open HostTensor
open CudaTensor
open CudaKernels
open Console

inl test_mnist mnist_path =
    open AutoDiffOps
    open FeedforwardLayers
    inb mnist_tensors = load_mnist_tensors mnist_path |> from_host_tensors

    inl hidden_size = 10
    inl input_size = 784

    inb layers = init (sigmoid hidden_size) input_size
    inl network = with_error (Error.square) layers

    inl input, label = mnist_tensors.train_images, mnist_tensors.train_labels
    run {network input label minibatch_size=128; optimizer=Optimizers.sgd 0.01f32}

inl mnist_path = @"C:\ML Datasets\Mnist"
test_mnist mnist_path
    """

