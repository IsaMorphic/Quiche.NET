extern crate bindgen;

use std::env;
use std::path::PathBuf;

fn main() {

    let bindings = bindgen::Builder::default()
        .header("quiche.h")
        .generate()
        .expect("Unable to generate bindings");

    let out_path = PathBuf::from(env::var("OUT_DIR").unwrap());
    bindings
        .write_to_file(out_path.join("bindings.rs"))
        .expect("Couldn't write bindings!");

    // csbindgen code, generate both rust ffi and C# dll import
    csbindgen::Builder::default()
    .input_bindgen_file(out_path.join("bindings.rs")) // read from bindgen generated code
    .csharp_dll_name("libquiche")
    .csharp_namespace("Quiche")
    .generate_csharp_file("../libquiche.g.cs")
    .unwrap();
}