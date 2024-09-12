extern crate bindgen;

fn main() {
    // Tell cargo to look for shared libraries in the specified directory
    let profile = std::env::var("PROFILE").unwrap();
    match profile.as_str() {
        "debug" => println!("cargo:rustc-link-search=../quiche/target/debug"),
        "release" => println!("cargo:rustc-link-search=../quiche/target/release"),
        _ => (),
    }

    // Tell cargo to tell rustc to link the system quiche library
    // shared library.
    println!("cargo:rustc-link-lib=quiche");

    // Windows only
    #[cfg(target_os = "windows")] 
    {
        println!("cargo:rustc-link-lib=crypt32");
        println!("cargo:rustc-link-lib=bcrypt");
    }

    // The bindgen::Builder is the main entry point
    // to bindgen, and lets you build up options for
    // the resulting bindings.
    bindgen::Builder::default()
    // The input header we would like to generate
    // bindings for.
    .header("include/quiche.h")
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
    .rust_file_header("use super::quiche::*;")     // import bindgen generated modules(struct/method)
    .rust_method_prefix("__")
    .csharp_entry_point_prefix("__")
    .csharp_dll_name("quiche")
    .csharp_namespace("Quiche")
    .generate_to_file("src/quiche_ffi.rs", "../NativeMethods.g.cs")
    .unwrap();
}