extern crate bindgen;

fn main() {
    // The bindgen::Builder is the main entry point
    // to bindgen, and lets you build up options for
    // the resulting bindings.
    let builder = 
    if std::env::var_os("CARGO_FEATURE_ANDROID").is_some() 
    {
        bindgen::Builder::default()
        .clang_arg("--sysroot=/usr/local/share/android-ndk/sysroot")
    } else { bindgen::Builder::default() };
    // The input header we would like to generate
    // bindings for.
    builder.header("include/quiche.h")
    // Tell cargo to invalidate the built crate whenever any of the
    // included header files changed.
    .parse_callbacks(Box::new(bindgen::CargoCallbacks::new()))
    // Finish the builder and generate the bindings.
    .generate()
    // Unwrap the Result and panic on failure.
    .expect("Unable to generate bindings")
    .write_to_file("src/quiche.rs")
    .expect("Couldn't write bindings!");


    // csbindgen code, generate both rust ffi and C# dll import
    csbindgen::Builder::default()
    .input_bindgen_file("src/quiche.rs") // read from bindgen generated code
    .method_filter(|x| { x.starts_with("quiche") } )
    .csharp_dll_name("quiche")
    .csharp_namespace("Quiche")
    .generate_csharp_file("../NativeMethods.g.cs")
    .unwrap();
}