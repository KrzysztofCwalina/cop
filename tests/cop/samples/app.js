// Test fixture: JavaScript types and statements.
// Types: Greeter, App (2)
// console.log calls: 1
// eval calls: 1
// alert calls: 1

class Greeter {
    greet(name) {
        console.log(`Hello ${name}`);
    }
}

class App {
    run() {
        eval("1+1");
    }
}

function setup() {
    alert("ready");
}
