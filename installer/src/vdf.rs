//! Valve's KeyValues formats: the text one (libraryfolders.vdf, appmanifest_*.acf, config.vdf)
//! and the binary one Steam uses for shortcuts.vdf.

use std::io;

/// A parsed text KeyValues node. Keys are matched case-insensitively, as Steam does.
#[derive(Debug, Clone)]
pub enum Kv {
    Str(String),
    Obj(Vec<(String, Kv)>),
}

impl Kv {
    pub fn get(&self, key: &str) -> Option<&Kv> {
        match self {
            Kv::Obj(entries) => entries
                .iter()
                .find(|(k, _)| k.eq_ignore_ascii_case(key))
                .map(|(_, v)| v),
            Kv::Str(_) => None,
        }
    }

    pub fn path(&self, keys: &[&str]) -> Option<&Kv> {
        keys.iter().try_fold(self, |node, key| node.get(key))
    }

    pub fn str(&self) -> Option<&str> {
        match self {
            Kv::Str(s) => Some(s),
            Kv::Obj(_) => None,
        }
    }

    pub fn entries(&self) -> &[(String, Kv)] {
        match self {
            Kv::Obj(entries) => entries,
            Kv::Str(_) => &[],
        }
    }
}

/// Parses a text KeyValues document into its root object.
pub fn parse(text: &str) -> Kv {
    let mut tokens = Tokens {
        chars: text.chars().peekable(),
    };
    Kv::Obj(parse_object(&mut tokens))
}

fn parse_object(tokens: &mut Tokens) -> Vec<(String, Kv)> {
    let mut entries = Vec::new();
    while let Some(token) = tokens.next() {
        let key = match token {
            Token::Close => break,
            Token::Open => continue,
            Token::Str(s) => s,
        };
        match tokens.next() {
            Some(Token::Open) => entries.push((key, Kv::Obj(parse_object(tokens)))),
            Some(Token::Str(value)) => entries.push((key, Kv::Str(value))),
            Some(Token::Close) | None => break,
        }
    }
    entries
}

enum Token {
    Open,
    Close,
    Str(String),
}

struct Tokens<'a> {
    chars: std::iter::Peekable<std::str::Chars<'a>>,
}

impl Iterator for Tokens<'_> {
    type Item = Token;

    fn next(&mut self) -> Option<Token> {
        loop {
            let c = *self.chars.peek()?;
            if c.is_whitespace() {
                self.chars.next();
            } else if c == '/' {
                // line comment
                while self.chars.next().is_some_and(|c| c != '\n') {}
            } else if c == '[' {
                // platform conditional like [$WIN32]: ignored
                while self.chars.next().is_some_and(|c| c != ']') {}
            } else {
                break;
            }
        }
        match self.chars.next()? {
            '{' => Some(Token::Open),
            '}' => Some(Token::Close),
            '"' => {
                let mut s = String::new();
                while let Some(c) = self.chars.next() {
                    match c {
                        '"' => break,
                        '\\' => match self.chars.next() {
                            Some('n') => s.push('\n'),
                            Some('t') => s.push('\t'),
                            Some(other) => s.push(other),
                            None => break,
                        },
                        c => s.push(c),
                    }
                }
                Some(Token::Str(s))
            }
            first => {
                let mut s = String::from(first);
                while let Some(&c) = self.chars.peek() {
                    if c.is_whitespace() || c == '{' || c == '}' || c == '"' {
                        break;
                    }
                    s.push(c);
                    self.chars.next();
                }
                Some(Token::Str(s))
            }
        }
    }
}

/// A node of a binary KeyValues document.
#[derive(Debug, Clone, PartialEq)]
pub enum Bin {
    Map(Vec<(String, Bin)>),
    Str(String),
    Int(u32),
    Float(f32),
    U64(u64),
}

impl Bin {
    pub fn get(&self, key: &str) -> Option<&Bin> {
        match self {
            Bin::Map(entries) => entries
                .iter()
                .find(|(k, _)| k.eq_ignore_ascii_case(key))
                .map(|(_, v)| v),
            _ => None,
        }
    }

    pub fn str(&self) -> Option<&str> {
        match self {
            Bin::Str(s) => Some(s),
            _ => None,
        }
    }
}

pub fn read_bin(data: &[u8]) -> io::Result<Vec<(String, Bin)>> {
    let mut pos = 0;
    read_map(data, &mut pos)
}

fn read_map(data: &[u8], pos: &mut usize) -> io::Result<Vec<(String, Bin)>> {
    let mut entries = Vec::new();
    loop {
        let kind = *data.get(*pos).ok_or_else(truncated)?;
        *pos += 1;
        if kind == 0x08 {
            return Ok(entries);
        }
        let key = read_cstr(data, pos)?;
        let value = match kind {
            0x00 => Bin::Map(read_map(data, pos)?),
            0x01 => Bin::Str(read_cstr(data, pos)?),
            0x02 => Bin::Int(u32::from_le_bytes(take(data, pos)?)),
            0x03 => Bin::Float(f32::from_le_bytes(take(data, pos)?)),
            0x07 => Bin::U64(u64::from_le_bytes(take(data, pos)?)),
            other => {
                return Err(io::Error::new(
                    io::ErrorKind::InvalidData,
                    format!("unknown binary VDF type {other:#x}"),
                ));
            }
        };
        entries.push((key, value));
    }
}

fn take<const N: usize>(data: &[u8], pos: &mut usize) -> io::Result<[u8; N]> {
    let bytes = data.get(*pos..*pos + N).ok_or_else(truncated)?;
    *pos += N;
    Ok(bytes.try_into().unwrap())
}

fn read_cstr(data: &[u8], pos: &mut usize) -> io::Result<String> {
    let end = data[*pos..]
        .iter()
        .position(|&b| b == 0)
        .ok_or_else(truncated)?;
    let s = String::from_utf8_lossy(&data[*pos..*pos + end]).into_owned();
    *pos += end + 1;
    Ok(s)
}

fn truncated() -> io::Error {
    io::Error::new(io::ErrorKind::UnexpectedEof, "truncated binary VDF")
}

pub fn write_bin(entries: &[(String, Bin)]) -> Vec<u8> {
    let mut out = Vec::new();
    write_map(&mut out, entries);
    out
}

fn write_map(out: &mut Vec<u8>, entries: &[(String, Bin)]) {
    for (key, value) in entries {
        let (kind, payload): (u8, Vec<u8>) = match value {
            Bin::Map(inner) => {
                let mut nested = Vec::new();
                write_map(&mut nested, inner);
                (0x00, nested)
            }
            Bin::Str(s) => (0x01, [s.as_bytes(), &[0]].concat()),
            Bin::Int(v) => (0x02, v.to_le_bytes().to_vec()),
            Bin::Float(v) => (0x03, v.to_le_bytes().to_vec()),
            Bin::U64(v) => (0x07, v.to_le_bytes().to_vec()),
        };
        out.push(kind);
        out.extend_from_slice(key.as_bytes());
        out.push(0);
        out.extend_from_slice(&payload);
    }
    out.push(0x08);
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn text_roundtrip() {
        let kv = parse(
            r#""libraryfolders" { "0" { "path" "/home/a/.local/share/Steam" // root
                "apps" { "3271280" "123" } } "1" { "path" "/mnt/Games \"x\"" } }"#,
        );
        let folders = kv.get("LibraryFolders").unwrap();
        assert_eq!(
            folders.path(&["0", "path"]).unwrap().str(),
            Some("/home/a/.local/share/Steam")
        );
        assert_eq!(
            folders.path(&["1", "path"]).unwrap().str(),
            Some("/mnt/Games \"x\"")
        );
        assert_eq!(folders.entries().len(), 2);
    }

    #[test]
    fn binary_roundtrip() {
        let doc = vec![(
            "shortcuts".to_string(),
            Bin::Map(vec![(
                "0".to_string(),
                Bin::Map(vec![
                    ("appid".into(), Bin::Int(0x9500_0001)),
                    ("AppName".into(), Bin::Str("Game".into())),
                    (
                        "tags".into(),
                        Bin::Map(vec![("0".into(), Bin::Str("VR".into()))]),
                    ),
                ]),
            )]),
        )];
        let bytes = write_bin(&doc);
        assert!(bytes.ends_with(&[0x08, 0x08, 0x08, 0x08]));
        assert_eq!(read_bin(&bytes).unwrap(), doc);
    }
}
