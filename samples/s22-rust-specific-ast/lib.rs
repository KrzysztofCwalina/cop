/// A drawable shape. (documented trait — OK)
pub trait Drawable {
    fn draw(&self);
}

// A public trait with no doc comment — flagged by the "documented traits" rule.
pub trait Serialize {
    fn serialize(&self) -> String;
}

/// A low-level allocator. SAFETY: implementors must uphold invariants.
pub unsafe trait Allocator {
    fn alloc(&self);
}

pub struct Widget {
    pub width: u32,
}

impl Widget {
    pub fn new() -> Self {
        Widget { width: 0 }
    }
}

// An unsafe impl — flagged by the "no unsafe" rule.
unsafe impl Send for Widget {}
