#[path = "../native/fixtures.rs"]
mod fixtures;

fn main() {
    let directory = std::env::args_os()
        .nth(1)
        .expect("usage: make-fixture <output directory>");
    fixtures::create(std::path::Path::new(&directory), 30.0, false);
}
